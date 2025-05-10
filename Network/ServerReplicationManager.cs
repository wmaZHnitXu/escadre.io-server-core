// File: Scripts/Server/Core/Network/ServerReplicationManager.cs
using System;
using System.Collections.Generic;
using System.IO; // For BinaryReader/Writer if not covered by other usings
using System.Linq;
using Core.Model;
using Core.Network;
using Core.Network.Proxies;
using Core.Logging;
using Core.Visibility;

namespace Core.Network
{
    public class ServerReplicationManager : IDisposable
    {
        private readonly Level _level;
        private readonly IServerNetworkLayer _networkLayer;
        private readonly VisibilityManager _visibilityManager;
        private readonly Dictionary<int, IServerProxy> _activeProxies = new();
        private readonly HashSet<int> _replicatingEntityIds = new(); // Tracks entities for which StartReplicating has been called
        private bool _isDisposed = false;

        public ServerReplicationManager(Level level, IServerNetworkLayer networkLayer, VisibilityManager visibilityManager)
        {
            _level = level ?? throw new ArgumentNullException(nameof(level));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _visibilityManager = visibilityManager ?? throw new ArgumentNullException(nameof(visibilityManager));
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
                entity.OnDeathEvent += HandleEntityDeath;
            }
            catch (Exception ex) { Logger.LogError($"[ServerReplicationManager] Error handling Entity Added {entity.Id}: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        private void HandleEntityDeath(Entity entity)
        {
            if (_isDisposed) return;
            Logger.Log($"[ServerReplicationManager] Entity Died: ID={entity.Id}. Unregistering Visibility & Sending FINAL Destroy.");
            entity.OnDeathEvent -= HandleEntityDeath;

            List<int> clientsToNotify = _visibilityManager.GetClientsSeeingEntity(entity.Id).ToList();
            _visibilityManager.UnregisterEntity(entity.Id); 

            if (_activeProxies.TryGetValue(entity.Id, out IServerProxy proxy))
            {
                if (_replicatingEntityIds.Contains(entity.Id))
                {
                    proxy.StopReplicating();
                    _replicatingEntityIds.Remove(entity.Id);
                    Logger.Log($"[ServerReplicationManager] Stopped replicating for dead entity {entity.Id}.");
                }
                _activeProxies.Remove(entity.Id);

                if (clientsToNotify.Any())
                {
                    proxy.SendDestroyMessage(clientsToNotify);
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
                        proxy.StartReplicating();
                        _replicatingEntityIds.Add(entityId);
                        Logger.Log($"[ServerReplicationManager] Started replicating for entity {entityId} as it entered PVS for client {clientId}.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[ServerReplicationManager] Error sending CreateEntity or starting replication for {entityId} to client {clientId}: {ex.Message}\nStackTrace: {ex.StackTrace}");
                }
            }
            else
            {
                Logger.LogWarning($"[ServerReplicationManager] HandleEntityEnteredPvs: Proxy not found for Entity {entityId} when trying to send CreateEntity to Client {clientId}.");
            }
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
                    var stillSeeingClients = _visibilityManager.GetClientsSeeingEntity(entityId);
                    if (!stillSeeingClients.Any()) 
                    {
                        proxy.StopReplicating();
                        _replicatingEntityIds.Remove(entityId);
                        Logger.Log($"[ServerReplicationManager] Stopped replicating for entity {entityId} as it left PVS for all clients.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[ServerReplicationManager] Error sending DestroyEntity or stopping replication for {entityId} to client {clientId}: {ex.Message}\nStackTrace: {ex.StackTrace}");
            }
        }

        private void HandleClientMessage(int clientId, int entityId, MessageType messageType, BinaryReader payloadReader)
        {
            if (_isDisposed) return;
            if (_activeProxies.TryGetValue(entityId, out IServerProxy proxy))
            {
                switch (messageType)
                {
                    case MessageType.ClientSyncState:
                        bool needsCorrection = proxy.CheckClientSyncState(payloadReader);
                        if (needsCorrection)
                        {
                            _networkLayer.SendToClient(clientId, entityId, MessageType.UpdateState, proxy.SerializeCorrectionState);
                        }
                        break;
                    default:
                        Logger.LogWarning($"[ServerReplicationManager] Received unhandled MessageType ({messageType}) from ClientId={clientId} for Entity {entityId}.");
                        break;
                }
            }
            else
            {
                Logger.LogWarning($"[ServerReplicationManager] Received message ({messageType}) for unknown/inactive Entity ID: {entityId} from ClientId={clientId}.");
            }
        }

        public void UpdateCoreModel(float delta)
        {
            if (_isDisposed) return;
            try
            {
                _level.DoUpdate(delta);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[CoreComposer] Error during Level Update: {ex.Message}\nStackTrace: {ex.StackTrace}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Logger.Log("[ServerReplicationManager] Disposing...");
            if (_networkLayer != null) _networkLayer.OnClientMessageReceived -= HandleClientMessage;
            if (_visibilityManager != null)
            {
                _visibilityManager.EntityEnteredPvs -= HandleEntityEnteredPvs;
                _visibilityManager.EntityLeftPvs -= HandleEntityLeftPvs;
            }
            _level.OnEntityAddedEvent -= HandleEntityAdded;

            var proxyIds = new List<int>(_activeProxies.Keys);
            foreach (var id in proxyIds)
            {
                if (_activeProxies.TryGetValue(id, out IServerProxy proxy))
                {
                    if (_replicatingEntityIds.Contains(id))
                    {
                        proxy.StopReplicating();
                    }
                }
                // _visibilityManager?.UnregisterEntity(id); // UnregisterEntity is now called from HandleEntityDeath
            }
            _replicatingEntityIds.Clear();
            _activeProxies.Clear();
            Logger.Log("[ServerReplicationManager] Dispose complete.");
        }
    }
}