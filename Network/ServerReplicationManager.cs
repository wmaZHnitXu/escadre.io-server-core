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
        private readonly Dictionary<int, IServerProxy> _activeEntityProxies = new(); // Renamed for clarity
        private readonly Dictionary<int, EscadreProxy.ServerProxy> _activeEscadreProxies = new(); // For Escadre state
        private readonly HashSet<int> _replicatingEntityIds = new(); 
        private bool _isDisposed = false;
        private readonly Dictionary<int, ClientConnection> _clientConnections;
        private readonly IClock _serverClock;
        private readonly CoreComposer _coreComposer; // To subscribe to client registration events

        public ServerReplicationManager(
            Level level, IServerNetworkLayer networkLayer,
            VisibilityManager visibilityManager, Dictionary<int, ClientConnection> clientConnections,
            IClock serverClock, CoreComposer coreComposer) // Added CoreComposer
        {
            _level = level ?? throw new ArgumentNullException(nameof(level));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _visibilityManager = visibilityManager ?? throw new ArgumentNullException(nameof(visibilityManager));
            _clientConnections = clientConnections ?? throw new ArgumentNullException(nameof(clientConnections));
            _serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock)); 
            _coreComposer = coreComposer ?? throw new ArgumentNullException(nameof(coreComposer));

            _level.OnEntityAddedEvent += HandleEntityAddedToLevel; 
            _networkLayer.OnClientMessageReceived += HandleClientMessage_GameLogic;
            _visibilityManager.EntityEnteredPvs += HandleEntityEnteredPvs;
            _visibilityManager.EntityLeftPvs += HandleEntityLeftPvs;

            _coreComposer.ClientRegisteredEvent += HandleClientRegistered;
            _coreComposer.ClientUnregisteredEvent += HandleClientUnregistered;

            // Initialize escadre proxies for already connected clients (if any, e.g. during a hot reload scenario)
            foreach(var clientConn in _clientConnections.Values)
            {
                HandleClientRegistered(clientConn);
            }

            Logger.Log("[ServerReplicationManager] Initialized.");
        }

        private void HandleClientRegistered(ClientConnection clientConnection)
        {
            if (clientConnection.EscadreInstance != null && !_activeEscadreProxies.ContainsKey(clientConnection.ClientId))
            {
                var escadreProxy = new EscadreProxy.ServerProxy(clientConnection.EscadreInstance, _level, _networkLayer);
                _activeEscadreProxies.Add(clientConnection.ClientId, escadreProxy);
                escadreProxy.StartReplicatingToOwner();
            }
        }

        private void HandleClientUnregistered(ClientConnection clientConnection)
        {
            if (_activeEscadreProxies.TryGetValue(clientConnection.ClientId, out var escadreProxy))
            {
                escadreProxy.StopReplicating();
                _activeEscadreProxies.Remove(clientConnection.ClientId);
            }
        }


        private void HandleEntityAddedToLevel(Entity entity)
        {
            if (_isDisposed || entity.IsDead || _activeEntityProxies.ContainsKey(entity.Id)) return;
            Logger.Log($"[SRM] Entity Added to Level: ID={entity.Id}, Type={entity.EntityType}. Creating Proxy & Registering Visibility.");
            try
            {
                IServerProxy proxy = ServerProxyFactory.CreateServerProxy(entity, _networkLayer);
                _activeEntityProxies.Add(entity.Id, proxy);
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

            if (_activeEntityProxies.TryGetValue(entity.Id, out IServerProxy proxy))
            {
                if (_replicatingEntityIds.Contains(entity.Id))
                {
                    proxy.StopReplicating(); 
                    _replicatingEntityIds.Remove(entity.Id);
                    Logger.Log($"[SRM] Stopped replicating for dead entity {entity.Id}.");
                }
                _activeEntityProxies.Remove(entity.Id); 

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
            if (_activeEntityProxies.TryGetValue(entityId, out IServerProxy proxy))
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

                if (_replicatingEntityIds.Contains(entityId) && _activeEntityProxies.TryGetValue(entityId, out IServerProxy proxy))
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

        private void HandleClientMessage_GameLogic(int sourceNetworkId, int contextId, MessageType messageType, BinaryReader reader)
        {
            if (_isDisposed) return;

            if (messageType == MessageType._ClientConnectRequest || messageType == MessageType._ClientDisconnect)
            {
                return; 
            }
            
            int gameClientId = sourceNetworkId; 

            if (!_clientConnections.TryGetValue(gameClientId, out ClientConnection clientConnection))
            {
                Logger.LogWarning($"[SRM] Received game logic message (Type: {messageType}) from unestablished/unknown GameClient ID: {gameClientId} (derived from SourceNetworkID: {sourceNetworkId}). Ignoring.");
                return;
            }

            float currentTime = _serverClock.CurrentTime;
            bool success; // For request results

            switch (messageType)
            {
                case MessageType._ClientSyncState:
                    if (_activeEntityProxies.TryGetValue(contextId, out IServerProxy proxy))
                    {
                        if (proxy.CheckClientSyncState(reader))
                        {
                            _networkLayer.SendToClient(gameClientId, contextId, MessageType.UpdateState, proxy.SerializeCorrectionState);
                        }
                    }
                    else { Logger.LogWarning($"[SRM CId={gameClientId}] _ClientSyncState for unknown EntityProxy ID: {contextId}."); }
                    break;
                // Escadre General Commands (contextId is usually 0 or ignored for these, action is on clientConnection's Escadre)
                case MessageType._SetCourse: 
                    try { clientConnection.RequestSetCourse(SerializationUtils.ReadVector2(reader), currentTime); } 
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _SetCourse: {ex.Message}"); } 
                    break;
                case MessageType._AttackEscadre: 
                    try { clientConnection.RequestAttackEscadre(reader.ReadInt32(), currentTime); } 
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _AttackEscadre: {ex.Message}"); } 
                    break;
                case MessageType._CancelAttack: 
                    try { clientConnection.RequestCancelAttack(); } 
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _CancelAttack: {ex.Message}"); } 
                    break;
                
                // Shop & Formation Commands
                case MessageType._RequestBuyShip:
                    try
                    {
                        int shipDesignId = reader.ReadInt32();
                        Vector2 preferredOffset = SerializationUtils.ReadVector2(reader);
                        success = clientConnection.RequestBuyShip(shipDesignId, preferredOffset, currentTime);
                        // Optional: Send _BuyShipResult back to client
                        // _networkLayer.SendToClient(gameClientId, 0, MessageType._BuyShipResult, w => w.Write(success));
                        if (!success) Logger.LogWarning($"[SRM CId={gameClientId}] _RequestBuyShip failed for design {shipDesignId}.");
                    }
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _RequestBuyShip: {ex.Message}"); }
                    break;
                case MessageType._RequestUpgradeShip:
                    try
                    {
                        int shipEntityIdToUpgrade = reader.ReadInt32();
                        success = clientConnection.RequestUpgradeShip(shipEntityIdToUpgrade);
                        // Optional: Send _UpgradeShipResult
                        if (!success) Logger.LogWarning($"[SRM CId={gameClientId}] _RequestUpgradeShip failed for ship {shipEntityIdToUpgrade}.");
                    }
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _RequestUpgradeShip: {ex.Message}"); }
                    break;
                case MessageType._RequestSetFormation:
                    try
                    {
                        int slotCount = reader.ReadInt32();
                        var newLayout = new List<Tuple<int, Vector2>>();
                        for(int i=0; i<slotCount; i++)
                        {
                            newLayout.Add(new Tuple<int, Vector2>(reader.ReadInt32(), SerializationUtils.ReadVector2(reader)));
                        }
                        success = clientConnection.RequestSetFormation(newLayout, currentTime);
                        // Optional: Send _SetFormationResult
                        if (!success) Logger.LogWarning($"[SRM CId={gameClientId}] _RequestSetFormation failed.");
                    }
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _RequestSetFormation: {ex.Message}"); }
                    break;

                default: 
                    Logger.LogWarning($"[SRM CId={gameClientId}] Received unhandled GameLogic MessageType ({messageType}) for Context {contextId}."); 
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
            if (_coreComposer != null) { _coreComposer.ClientRegisteredEvent -= HandleClientRegistered; _coreComposer.ClientUnregisteredEvent -= HandleClientUnregistered; }


            var entityProxyIds = new List<int>(_activeEntityProxies.Keys);
            foreach (var id in entityProxyIds) {
                if (_activeEntityProxies.TryGetValue(id, out IServerProxy proxy)) {
                    if (_replicatingEntityIds.Contains(id)) { proxy.StopReplicating(); }
                    if(_level.TryGetEntity(id, out Entity entity)) { 
                        entity.OnDeathEvent -= HandleModelEntityDeath; 
                    }
                }
                _visibilityManager?.UnregisterEntity(id); 
            }
            _replicatingEntityIds.Clear();
            _activeEntityProxies.Clear();

            var escadreProxyClientIds = new List<int>(_activeEscadreProxies.Keys);
            foreach(var clientId in escadreProxyClientIds)
            {
                if(_activeEscadreProxies.TryGetValue(clientId, out var escadreProxy))
                {
                    escadreProxy.StopReplicating();
                }
            }
            _activeEscadreProxies.Clear();

            Logger.Log("[ServerReplicationManager] Dispose complete.");
        }
    }
}