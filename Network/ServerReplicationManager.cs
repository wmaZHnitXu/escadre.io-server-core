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
using Core.Primitives; // For Vector2 in SetCourse deserialization

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

        public ServerReplicationManager(
            Level level,
            IServerNetworkLayer networkLayer,
            VisibilityManager visibilityManager,
            Dictionary<int, ClientConnection> clientConnections)
        {
            _level = level ?? throw new ArgumentNullException(nameof(level));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _visibilityManager = visibilityManager ?? throw new ArgumentNullException(nameof(visibilityManager));
            _clientConnections = clientConnections ?? throw new ArgumentNullException(nameof(clientConnections));

            _level.OnEntityAddedEvent += HandleEntityAdded;
            _networkLayer.OnClientMessageReceived += HandleClientMessage;
            _visibilityManager.EntityEnteredPvs += HandleEntityEnteredPvs;
            _visibilityManager.EntityLeftPvs += HandleEntityLeftPvs;
            Logger.Log("[ServerReplicationManager] Initialized.");
        }

        private void HandleEntityAdded(Entity entity)
        {
            if (_isDisposed || entity.IsDead || _activeProxies.ContainsKey(entity.Id)) return;
            Logger.Log($"[ServerReplicationManager] Entity Added: ID={entity.Id}, Type={entity.EntityType}. Creating Proxy & Registering Visibility.");
            try
            {
                IServerProxy proxy = ServerProxyFactory.CreateServerProxy(entity, _networkLayer);
                _activeProxies.Add(entity.Id, proxy);
                _visibilityManager.RegisterEntity(entity);
                entity.OnDeathEvent += HandleEntityDeath; // Subscribe to core entity's death
            }
            catch (Exception ex) { Logger.LogError($"[ServerReplicationManager] Error handling Entity Added {entity.Id}: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        private void HandleEntityDeath(Entity entity)
        {
            if (_isDisposed) return;
            Logger.Log($"[ServerReplicationManager] Entity Died: ID={entity.Id}. Unregistering Visibility & Sending FINAL Destroy.");
            entity.OnDeathEvent -= HandleEntityDeath; // Unsubscribe

            List<int> clientsToNotify = _visibilityManager.GetClientsSeeingEntity(entity.Id).ToList();
            _visibilityManager.UnregisterEntity(entity.Id);

            if (_activeProxies.TryGetValue(entity.Id, out IServerProxy proxy))
            {
                if (_replicatingEntityIds.Contains(entity.Id))
                {
                    proxy.StopReplicating(); // Calls internal event unsubscriptions
                    _replicatingEntityIds.Remove(entity.Id);
                    Logger.Log($"[ServerReplicationManager] Stopped replicating for dead entity {entity.Id}.");
                }
                _activeProxies.Remove(entity.Id);

                if (clientsToNotify.Any())
                {
                    proxy.SendDestroyMessage(clientsToNotify); // Use IServerProxy method
                }
            }
            else
            {
                Logger.LogWarning($"[ServerReplicationManager] HandleEntityDeath: Proxy for {entity.Id} not found when trying to cleanup.");
            }
        }

        private void HandleEntityEnteredPvs(int clientId, int entityId)
        {
            if (_isDisposed) return;
            if (_activeProxies.TryGetValue(entityId, out IServerProxy proxy))
            {
                Logger.Log($"[ServerReplicationManager] Entity {entityId} entered PVS for Client {clientId}. Sending CreateEntity.");
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
                        proxy.StartReplicating(); // Calls internal event subscriptions
                        _replicatingEntityIds.Add(entityId);
                        Logger.Log($"[ServerReplicationManager] Started replicating for entity {entityId} as it entered PVS for client {clientId}.");
                    }
                }
                catch (Exception ex) { Logger.LogError($"[ServerReplicationManager] Error sending CreateEntity or starting replication for {entityId} to client {clientId}: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
            }
            else { Logger.LogWarning($"[ServerReplicationManager] HandleEntityEnteredPvs: Proxy not found for Entity {entityId} when trying to send CreateEntity to Client {clientId}."); }
        }

        private void HandleEntityLeftPvs(int clientId, int entityId)
        {
            if (_isDisposed) return;
            Logger.Log($"[ServerReplicationManager] Entity {entityId} left PVS for Client {clientId}. Sending DestroyEntity.");
            try
            {
                _networkLayer.SendToClient(clientId, entityId, MessageType.DestroyEntity, writer => { /* No payload */ });
                if (_replicatingEntityIds.Contains(entityId) && _activeProxies.TryGetValue(entityId, out IServerProxy proxy))
                {
                    if (!_visibilityManager.GetClientsSeeingEntity(entityId).Any()) // No other clients see it
                    {
                        proxy.StopReplicating();
                        _replicatingEntityIds.Remove(entityId);
                        Logger.Log($"[ServerReplicationManager] Stopped replicating for entity {entityId} as it left PVS for all clients.");
                    }
                }
            }
            catch (Exception ex) { Logger.LogError($"[ServerReplicationManager] Error sending DestroyEntity or stopping replication for {entityId} to client {clientId}: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        private void HandleClientMessage(int clientId, int entityId, MessageType messageType, BinaryReader reader)
        {
            if (_isDisposed) return;
            if (!_clientConnections.TryGetValue(clientId, out ClientConnection clientConnection))
            {
                Logger.LogWarning($"[ServerReplicationManager] Received message from unknown ClientId: {clientId}. Type: {messageType}");
                return;
            }

            // Logger.Log($"[SRM CId={clientId}] MsgForEntity={entityId}, Type={messageType}");

            switch (messageType)
            {
                case MessageType._ClientSyncState: // Renamed
                    if (_activeProxies.TryGetValue(entityId, out IServerProxy proxy))
                    {
                        bool needsCorrection = proxy.CheckClientSyncState(reader);
                        if (needsCorrection)
                        {
                            _networkLayer.SendToClient(clientId, entityId, MessageType.UpdateState, proxy.SerializeCorrectionState);
                        }
                    }
                    else { Logger.LogWarning($"[SRM CId={clientId}] _ClientSyncState for unknown EntityProxy ID: {entityId}."); }
                    break;

                case MessageType._SetCourse:
                    try { Vector2 destination = SerializationUtils.ReadVector2(reader); clientConnection.RequestSetCourse(destination); }
                    catch (Exception ex) { Logger.LogError($"[SRM CId={clientId}] Error processing _SetCourse: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
                    break;
                case MessageType._AttackEscadre:
                    try { int targetOwnerClientId = reader.ReadInt32(); clientConnection.RequestAttackEscadre(targetOwnerClientId); }
                    catch (Exception ex) { Logger.LogError($"[SRM CId={clientId}] Error processing _AttackEscadre: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
                    break;
                case MessageType._CancelAttack:
                    try { clientConnection.RequestCancelAttack(); }
                    catch (Exception ex) { Logger.LogError($"[SRM CId={clientId}] Error processing _CancelAttack: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
                    break;
                case MessageType._UpgradeShip:
                    try { int shipToUpgradeId = reader.ReadInt32(); clientConnection.RequestUpgradeShip(shipToUpgradeId); }
                    catch (Exception ex) { Logger.LogError($"[SRM CId={clientId}] Error processing _UpgradeShip: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
                    break;
                case MessageType._BuyShip:
                    try { int shipDesignId = reader.ReadInt32(); clientConnection.RequestBuyShip(shipDesignId); }
                    catch (Exception ex) { Logger.LogError($"[SRM CId={clientId}] Error processing _BuyShip: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
                    break;
                default:
                    Logger.LogWarning($"[SRM CId={clientId}] Received unhandled MessageType ({messageType}) for Entity {entityId}.");
                    break;
            }
        }

        public void UpdateCoreModel(float delta)
        {
            if (_isDisposed) return;
            try { _level.DoUpdate(delta); }
            catch (Exception ex) { Logger.LogError($"[ServerReplicationManager] Error during Level Update: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        public void Dispose()
        {
            if (_isDisposed) return; _isDisposed = true;
            Logger.Log("[ServerReplicationManager] Disposing...");
            if (_networkLayer != null) _networkLayer.OnClientMessageReceived -= HandleClientMessage;
            if (_visibilityManager != null) { _visibilityManager.EntityEnteredPvs -= HandleEntityEnteredPvs; _visibilityManager.EntityLeftPvs -= HandleEntityLeftPvs; }
            if (_level != null) _level.OnEntityAddedEvent -= HandleEntityAdded;

            var proxyIds = new List<int>(_activeProxies.Keys);
            foreach (var id in proxyIds) {
                if (_activeProxies.TryGetValue(id, out IServerProxy proxy)) {
                    if (_replicatingEntityIds.Contains(id)) { proxy.StopReplicating(); }
                    // Ensure entity death handler is removed if entity still somehow exists
                    // This might be redundant if HandleEntityDeath always fires or proxy.StopReplicating handles it.
                    if(_level.TryGetEntity(id, out Entity entity)) { entity.OnDeathEvent -= HandleEntityDeath; }
                }
                _visibilityManager?.UnregisterEntity(id);
            }
            _replicatingEntityIds.Clear();
            _activeProxies.Clear();
            Logger.Log("[ServerReplicationManager] Dispose complete.");
        }
    }
}