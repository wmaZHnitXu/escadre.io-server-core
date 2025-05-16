// File: Scripts/Server/Core/Network/ServerReplicationManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core.Model;
using Core.Network;
using Core.Network.Proxies;
using Core.Logging;
using Core.Visibility;
using Core.Session;
using Core.Primitives;

namespace Core.Network
{
    public class ServerReplicationManager : IDisposable
    {
        private readonly Level _level;
        private readonly IServerNetworkLayer _networkLayer;
        private readonly VisibilityManager _visibilityManager;
        private readonly Dictionary<int, IServerProxy> _activeProxies = new();
        private readonly HashSet<int> _replicatingEntityIds = new(); // Tracks IDs for which StartReplicating was called
        private bool _isDisposed = false;
        private readonly Dictionary<int, ClientConnection> _clientConnections;

        public ServerReplicationManager(
            Level level, IServerNetworkLayer networkLayer,
            VisibilityManager visibilityManager, Dictionary<int, ClientConnection> clientConnections)
        {
            _level = level ?? throw new ArgumentNullException(nameof(level));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _visibilityManager = visibilityManager ?? throw new ArgumentNullException(nameof(visibilityManager));
            _clientConnections = clientConnections ?? throw new ArgumentNullException(nameof(clientConnections));

            _level.OnEntityAddedEvent += HandleEntityAddedToLevel; // Renamed for clarity
            _networkLayer.OnClientMessageReceived += HandleClientMessage;
            _visibilityManager.EntityEnteredPvs += HandleEntityEnteredPvs;
            _visibilityManager.EntityLeftPvs += HandleEntityLeftPvs;
            Logger.Log("[ServerReplicationManager] Initialized.");
        }

        private void HandleEntityAddedToLevel(Entity entity)
        {
            if (_isDisposed || entity.IsDead || _activeProxies.ContainsKey(entity.Id)) return;
            Logger.Log($"[SRM] Entity Added to Level: ID={entity.Id}, Type={entity.EntityType}. Creating Proxy & Registering Visibility.");
            try
            {
                IServerProxy proxy = ServerProxyFactory.CreateServerProxy(entity, _networkLayer);
                _activeProxies.Add(entity.Id, proxy);
                _visibilityManager.RegisterEntity(entity); // Register with visibility system
                // Proxy subscribes to entity.OnDeathEvent in its StartReplicating.
                // We need to subscribe here too, to catch deaths of entities that might not yet be replicated to any client.
                entity.OnDeathEvent += HandleModelEntityDeath;
            }
            catch (Exception ex) { Logger.LogError($"[SRM] Error handling Entity Added {entity.Id}: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        // This handles death from the Core.Model.Entity perspective
        private void HandleModelEntityDeath(Entity entity)
        {
            if (_isDisposed) return;
            Logger.Log($"[SRM] Model Entity Died: ID={entity.Id}, Type={entity.EntityType}. Cleaning up proxy and visibility.");
            entity.OnDeathEvent -= HandleModelEntityDeath; // Unsubscribe

            // Note: MessageType.DestroyEntity (loud kill signal) is sent by the BaseServerProxy itself
            // when it receives the OnDestructionEvent from the entity.

            List<int> clientsThatSawEntity = _visibilityManager.GetClientsSeeingEntity(entity.Id).ToList();
            _visibilityManager.UnregisterEntity(entity.Id); // Remove from PVS calculations

            if (_activeProxies.TryGetValue(entity.Id, out IServerProxy proxy))
            {
                if (_replicatingEntityIds.Contains(entity.Id))
                {
                    proxy.StopReplicating(); // Unsubscribes proxy from entity events
                    _replicatingEntityIds.Remove(entity.Id);
                    Logger.Log($"[SRM] Stopped replicating for dead entity {entity.Id}.");
                }
                _activeProxies.Remove(entity.Id); // Remove proxy from active list

                if (clientsThatSawEntity.Any())
                {
                    // Send VanishEntity to tell clients to actually remove the proxy
                    Logger.Log($"[SRM] Sending VanishEntity for dead entity {entity.Id} to clients: [{string.Join(",", clientsThatSawEntity)}]");
                    proxy.SendVanishMessage(clientsThatSawEntity);
                }
            }
            else { Logger.LogWarning($"[SRM] HandleModelEntityDeath: Proxy for {entity.Id} not found."); }
        }


        private void HandleEntityEnteredPvs(int clientId, int entityId)
        {
            if (_isDisposed) return;
            if (_activeProxies.TryGetValue(entityId, out IServerProxy proxy))
            {
                // Ensure entity is not dead before sending Create message
                if (_level.TryGetEntity(entityId, out Entity ent) && ent.IsDead)
                {
                    Logger.LogWarning($"[SRM] Entity {entityId} entered PVS for Client {clientId}, but is already dead. Not sending CreateEntity.");
                    // Optionally send VanishEntity if client might have an old record, though unlikely here.
                    return;
                }

                Logger.Log($"[SRM] Entity {entityId} ({proxy.EntityType}) entered PVS for Client {clientId}. Sending CreateEntity.");
                try
                {
                    _networkLayer.SendToClient(clientId, entityId, MessageType.CreateEntity, writer =>
                    {
                        writer.Write((byte)proxy.EntityType);
                        SerializationUtils.WriteVector3(writer, proxy.Position);
                        SerializationUtils.WriteQuaternion(writer, proxy.Rotation);
                        proxy.SerializeSpecificInitialState(writer);
                    });

                    if (!_replicatingEntityIds.Contains(entityId)) // If first client to see it
                    {
                        proxy.StartReplicating(); // Proxy subscribes to entity events (like OnDestruction)
                        _replicatingEntityIds.Add(entityId);
                        Logger.Log($"[SRM] Started replicating events for entity {entityId}.");
                    }
                }
                catch (Exception ex) { Logger.LogError($"[SRM] Error sending CreateEntity for {entityId} to client {clientId}: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
            }
            else { Logger.LogWarning($"[SRM] HandleEntityEnteredPvs: Proxy not found for Entity {entityId}."); }
        }

        private void HandleEntityLeftPvs(int clientId, int entityId)
        {
            if (_isDisposed) return;
            Logger.Log($"[SRM] Entity {entityId} left PVS for Client {clientId}. Sending VanishEntity.");
            try
            {
                // Send VanishEntity to this specific client
                _networkLayer.SendToClient(clientId, entityId, MessageType.VanishEntity, writer => { /* No payload */ });

                // If no other clients see this entity anymore, stop its proxy from replicating events
                if (_replicatingEntityIds.Contains(entityId) && _activeProxies.TryGetValue(entityId, out IServerProxy proxy))
                {
                    if (!_visibilityManager.GetClientsSeeingEntity(entityId).Any())
                    {
                        proxy.StopReplicating();
                        _replicatingEntityIds.Remove(entityId);
                        Logger.Log($"[SRM] Stopped replicating events for entity {entityId} (left all PVS).");
                    }
                }
            }
            catch (Exception ex) { Logger.LogError($"[SRM] Error processing EntityLeftPvs for {entityId} to client {clientId}: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }


        private void HandleClientMessage(int clientId, int entityId, MessageType messageType, BinaryReader reader)
        {
            // ... (message handling logic remains the same) ...
            if (_isDisposed) return;
            if (!_clientConnections.TryGetValue(clientId, out ClientConnection clientConnection))
            {
                Logger.LogWarning($"[SRM] Received message from unknown ClientId: {clientId}. Type: {messageType}");
                return;
            }

            switch (messageType)
            {
                case MessageType._ClientSyncState:
                    if (_activeProxies.TryGetValue(entityId, out IServerProxy proxy))
                    {
                        if (proxy.CheckClientSyncState(reader))
                        {
                            _networkLayer.SendToClient(clientId, entityId, MessageType.UpdateState, proxy.SerializeCorrectionState);
                        }
                    }
                    else { Logger.LogWarning($"[SRM CId={clientId}] _ClientSyncState for unknown EntityProxy ID: {entityId}."); }
                    break;
                case MessageType._SetCourse: try { clientConnection.RequestSetCourse(SerializationUtils.ReadVector2(reader)); } catch (Exception ex) { Logger.LogError($"[SRM CId={clientId}] Error processing _SetCourse: {ex.Message}"); } break;
                case MessageType._AttackEscadre: try { clientConnection.RequestAttackEscadre(reader.ReadInt32()); } catch (Exception ex) { Logger.LogError($"[SRM CId={clientId}] Error processing _AttackEscadre: {ex.Message}"); } break;
                case MessageType._CancelAttack: try { clientConnection.RequestCancelAttack(); } catch (Exception ex) { Logger.LogError($"[SRM CId={clientId}] Error processing _CancelAttack: {ex.Message}"); } break;
                case MessageType._UpgradeShip: try { clientConnection.RequestUpgradeShip(reader.ReadInt32()); } catch (Exception ex) { Logger.LogError($"[SRM CId={clientId}] Error processing _UpgradeShip: {ex.Message}"); } break;
                case MessageType._BuyShip: try { clientConnection.RequestBuyShip(reader.ReadInt32()); } catch (Exception ex) { Logger.LogError($"[SRM CId={clientId}] Error processing _BuyShip: {ex.Message}"); } break;
                default: Logger.LogWarning($"[SRM CId={clientId}] Received unhandled MessageType ({messageType}) for Entity {entityId}."); break;
            }
        }

        public void UpdateCoreModel(float delta)
        {
            if (_isDisposed) return;
            try { _level.DoUpdate(delta); }
            catch (Exception ex) { Logger.LogError($"[SRM] Error during Level Update: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        public void Dispose()
        {
            if (_isDisposed) return; _isDisposed = true;
            Logger.Log("[ServerReplicationManager] Disposing...");
            if (_networkLayer != null) _networkLayer.OnClientMessageReceived -= HandleClientMessage;
            if (_visibilityManager != null) { _visibilityManager.EntityEnteredPvs -= HandleEntityEnteredPvs; _visibilityManager.EntityLeftPvs -= HandleEntityLeftPvs; }
            if (_level != null) _level.OnEntityAddedEvent -= HandleEntityAddedToLevel;

            var proxyIds = new List<int>(_activeProxies.Keys);
            foreach (var id in proxyIds) {
                if (_activeProxies.TryGetValue(id, out IServerProxy proxy)) {
                    if (_replicatingEntityIds.Contains(id)) { proxy.StopReplicating(); }
                    if(_level.TryGetEntity(id, out Entity entity)) { entity.OnDeathEvent -= HandleModelEntityDeath; entity.OnDestructionEvent -= proxy.GetType().GetMethod("HandleEntityLoudDestruction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.CreateDelegate(typeof(Action<Entity>), proxy) as Action<Entity>; /*More complex cleanup if proxy subscribed directly*/ }
                }
                _visibilityManager?.UnregisterEntity(id); // Ensure all entities are unregistered
            }
            _replicatingEntityIds.Clear();
            _activeProxies.Clear();
            Logger.Log("[ServerReplicationManager] Dispose complete.");
        }
    }
}