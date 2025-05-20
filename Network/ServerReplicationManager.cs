// File: Core/Network/ServerReplicationManager.cs
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
using Core.Time; 

namespace Core.Network
{
    public class ServerReplicationManager : IDisposable
    {
        private readonly Level _level;
        private readonly IServerNetworkLayer _networkLayer;
        private readonly VisibilityManager _visibilityManager;
        private readonly Dictionary<int, IServerProxy> _activeProxies = new();
        private readonly HashSet<int> _replicatingEntityIds = new(); 
        private bool _isDisposed = false;
        private readonly Dictionary<int, ClientConnection> _clientConnections;
        private readonly IClock _serverClock; 

        public ServerReplicationManager(
            Level level, IServerNetworkLayer networkLayer,
            VisibilityManager visibilityManager, Dictionary<int, ClientConnection> clientConnections,
            IClock serverClock)
        {
            _level = level ?? throw new ArgumentNullException(nameof(level));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _visibilityManager = visibilityManager ?? throw new ArgumentNullException(nameof(visibilityManager));
            _clientConnections = clientConnections ?? throw new ArgumentNullException(nameof(clientConnections));
            _serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock)); 

            _level.OnEntityAddedEvent += HandleEntityAddedToLevel; 
            _networkLayer.OnClientMessageReceived += HandleClientMessage_GameLogic;
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
                _visibilityManager.RegisterEntity(entity); 
                entity.OnDeathEvent += HandleModelEntityDeath;
            }
            catch (Exception ex) { Logger.LogError($"[SRM] Error handling Entity Added {entity.Id}: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        private void HandleModelEntityDeath(Entity entity)
        {
            if (_isDisposed) return;
            Logger.Log($"[SRM] Model Entity Died: ID={entity.Id}, Type={entity.EntityType}. Cleaning up proxy and visibility.");
            entity.OnDeathEvent -= HandleModelEntityDeath; 

            List<int> clientsThatSawEntity = _visibilityManager.GetClientsSeeingEntity(entity.Id).ToList();
            _visibilityManager.UnregisterEntity(entity.Id); 

            if (_activeProxies.TryGetValue(entity.Id, out IServerProxy proxy))
            {
                if (_replicatingEntityIds.Contains(entity.Id))
                {
                    proxy.StopReplicating(); 
                    _replicatingEntityIds.Remove(entity.Id);
                    Logger.Log($"[SRM] Stopped replicating for dead entity {entity.Id}.");
                }
                _activeProxies.Remove(entity.Id); 

                if (clientsThatSawEntity.Any())
                {
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
                if (_level.TryGetEntity(entityId, out Entity ent) && ent.IsDead)
                {
                    Logger.LogWarning($"[SRM] Entity {entityId} entered PVS for Client {clientId}, but is already dead. Not sending CreateEntity.");
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

                    if (!_replicatingEntityIds.Contains(entityId)) 
                    {
                        proxy.StartReplicating(); 
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
                _networkLayer.SendToClient(clientId, entityId, MessageType.VanishEntity, writer => { /* No payload */ });

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

        private void HandleClientMessage_GameLogic(int sourceNetworkId, int entityIdContext, MessageType messageType, BinaryReader reader)
        {
            if (_isDisposed) return;

            // SRM ignores session management messages; CoreComposer handles them.
            if (messageType == MessageType._ClientConnectRequest || messageType == MessageType._ClientDisconnect)
            {
                return; 
            }

            // For game logic messages, SRM needs the gameClientId, not the sourceNetworkId,
            // if they can be different (which they can be).
            // CoreComposer maintains the mapping from sourceNetworkId to gameClientId.
            // SRM needs access to this mapping or needs the gameClientId passed to it.
            // For now, we assume sourceNetworkId IS the gameClientId for SRM's context AFTER connection.
            // This is a simplification in the mock setup. A real system would have a clear gameClientId context.
            // Let's use sourceNetworkId as the clientId for looking up ClientConnection for game messages.
            // This implies that after CoreComposer maps sourceNetworkId to gameClientId, all subsequent messages
            // from that sourceNetworkId are considered to be from that gameClientId.
            
            int gameClientId = sourceNetworkId; // Assuming sourceNetworkId is treated as gameClientId for SRM after connection.
                                                // This simplification might need review in a multi-client-instance-per-sourceNetworkId scenario.

            if (!_clientConnections.TryGetValue(gameClientId, out ClientConnection clientConnection))
            {
                Logger.LogWarning($"[SRM] Received game logic message (Type: {messageType}) from unestablished/unknown GameClient ID: {gameClientId} (derived from SourceNetworkID: {sourceNetworkId}). Ignoring.");
                return;
            }

            float currentTime = _serverClock.CurrentTime;

            switch (messageType)
            {
                case MessageType._ClientSyncState:
                    if (_activeProxies.TryGetValue(entityIdContext, out IServerProxy proxy))
                    {
                        if (proxy.CheckClientSyncState(reader))
                        {
                            _networkLayer.SendToClient(gameClientId, entityIdContext, MessageType.UpdateState, proxy.SerializeCorrectionState);
                        }
                    }
                    else { Logger.LogWarning($"[SRM CId={gameClientId}] _ClientSyncState for unknown EntityProxy ID: {entityIdContext}."); }
                    break;
                case MessageType._SetCourse: try { clientConnection.RequestSetCourse(SerializationUtils.ReadVector2(reader), currentTime); } catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _SetCourse: {ex.Message}"); } break;
                case MessageType._AttackEscadre: try { clientConnection.RequestAttackEscadre(reader.ReadInt32(), currentTime); } catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _AttackEscadre: {ex.Message}"); } break;
                case MessageType._CancelAttack: try { clientConnection.RequestCancelAttack(); } catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _CancelAttack: {ex.Message}"); } break;
                case MessageType._UpgradeShip: try { clientConnection.RequestUpgradeShip(reader.ReadInt32()); } catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _UpgradeShip: {ex.Message}"); } break;
                case MessageType._BuyShip: 
                    try 
                    { 
                        int shipDesignId = reader.ReadInt32();
                        Vector3 placeholderSpawnPosition = clientConnection.EscadreInstance != null ? 
                                                           clientConnection.EscadreInstance.CalculateCenterPoint() + new Vector3(5,0,5) : 
                                                           new Vector3( (gameClientId % 5) * 10f, 0, (gameClientId / 5) * 10f); 
                        clientConnection.RequestBuyShip(shipDesignId, placeholderSpawnPosition, currentTime); 
                    } 
                    catch (NotImplementedException nie)
                    {
                        Logger.LogWarning($"[SRM CId={gameClientId}] _BuyShip failed: {nie.Message}. This is expected until ShopManager is implemented.");
                    }
                    catch (Exception ex) 
                    { 
                        Logger.LogError($"[SRM CId={gameClientId}] Error processing _BuyShip: {ex.Message}"); 
                    } 
                    break;
                default: 
                    // This default case should ideally not be hit for valid game messages.
                    // If it is, it means a MessageType was sent that SRM's game logic doesn't recognize.
                    Logger.LogWarning($"[SRM CId={gameClientId}] Received unhandled GameLogic MessageType ({messageType}) for Entity {entityIdContext}. This might indicate a missing case in SRM's game message handler."); 
                    break;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return; _isDisposed = true;
            Logger.Log("[ServerReplicationManager] Disposing...");
            if (_networkLayer != null) _networkLayer.OnClientMessageReceived -= HandleClientMessage_GameLogic;
            if (_visibilityManager != null) { _visibilityManager.EntityEnteredPvs -= HandleEntityEnteredPvs; _visibilityManager.EntityLeftPvs -= HandleEntityLeftPvs; }
            if (_level != null) _level.OnEntityAddedEvent -= HandleEntityAddedToLevel;

            var proxyIds = new List<int>(_activeProxies.Keys);
            foreach (var id in proxyIds) {
                if (_activeProxies.TryGetValue(id, out IServerProxy proxy)) {
                    if (_replicatingEntityIds.Contains(id)) { proxy.StopReplicating(); }
                    if(_level.TryGetEntity(id, out Entity entity)) { 
                        entity.OnDeathEvent -= HandleModelEntityDeath; 
                    }
                }
                _visibilityManager?.UnregisterEntity(id); 
            }
            _replicatingEntityIds.Clear();
            _activeProxies.Clear();
            Logger.Log("[ServerReplicationManager] Dispose complete.");
        }
    }
}