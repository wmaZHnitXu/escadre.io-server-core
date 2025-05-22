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
        private readonly Dictionary<int, IServerProxy> _activeEntityProxies = new(); 
        // private readonly Dictionary<int, EscadreProxy.ServerProxy> _activeEscadreProxies = new(); // REMOVED
        private readonly HashSet<int> _replicatingEntityIds = new(); 
        private bool _isDisposed = false;
        private readonly Dictionary<int, ClientConnection> _clientConnections;
        private readonly IClock _serverClock;
        // private readonly CoreComposer _coreComposer; // REMOVED - No longer needed for specific EscadreProxy setup

        public ServerReplicationManager(
            Level level, IServerNetworkLayer networkLayer,
            VisibilityManager visibilityManager, Dictionary<int, ClientConnection> clientConnections,
            IClock serverClock) // Removed CoreComposer
        {
            _level = level ?? throw new ArgumentNullException(nameof(level));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _visibilityManager = visibilityManager ?? throw new ArgumentNullException(nameof(visibilityManager));
            _clientConnections = clientConnections ?? throw new ArgumentNullException(nameof(clientConnections));
            _serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock)); 
            // _coreComposer = coreComposer; // REMOVED

            _level.OnEntityAddedEvent += HandleEntityAddedToLevel; 
            _networkLayer.OnClientMessageReceived += HandleClientMessage_GameLogic;
            _visibilityManager.EntityEnteredPvs += HandleEntityEnteredPvs;
            _visibilityManager.EntityLeftPvs += HandleEntityLeftPvs;

            // No longer need to subscribe to CoreComposer.ClientRegisteredEvent for EscadreProxy management
            // _coreComposer.ClientRegisteredEvent += HandleClientRegistered; // REMOVED
            // _coreComposer.ClientUnregisteredEvent += HandleClientUnregistered; // REMOVED

            Logger.Log("[ServerReplicationManager] Initialized.");
        }

        // private void HandleClientRegistered(ClientConnection clientConnection) // REMOVED
        // {
        // // EscadreProxy is now an IServerProxy for an Escadre entity, handled by generic flow.
        // }

        // private void HandleClientUnregistered(ClientConnection clientConnection) // REMOVED
        // {
        // // EscadreProxy is an IServerProxy, cleaned up with other entity proxies.
        // }

        private void HandleEntityAddedToLevel(Entity entity)
        {
            if (_isDisposed || entity.IsDead || _activeEntityProxies.ContainsKey(entity.Id)) return;
            Logger.Log($"[SRM] Entity Added to Level: ID={entity.Id}, Type={entity.EntityType}. Creating Proxy & Registering Visibility.");
            try
            {
                // ServerProxyFactory will correctly create EscadreProxy.ServerProxy for Escadre entities
                IServerProxy proxy = ServerProxyFactory.CreateServerProxy(entity, _networkLayer);
                _activeEntityProxies.Add(entity.Id, proxy);
                // VisibilityManager.RegisterEntity(entity); // Now done by CoreComposer's Update loop
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
            _visibilityManager.UnregisterEntity(entity.Id); // This should be safe to call even if already unregistered by VM update cycle

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
                // This check is important because VisibilityManager might report PVS entry
                // slightly before Level processes the death and SRM cleans up the proxy.
                if (_level.TryGetEntity(entityId, out Entity ent) && ent.IsDead)
                {
                    Logger.LogWarning($"[SRM] Entity {entityId} entered PVS for Client {clientId}, but is already dead in Level. Not sending CreateEntity.");
                    return;
                }

                Logger.Log($"[SRM] Entity {entityId} ({proxy.EntityType}) entered PVS for Client {clientId}. Sending CreateEntity.");
                try
                {
                    // Context ID for CreateEntity is always the entityId itself
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
                        Logger.Log($"[SRM] Started replicating events for entity {entityId} (Type: {proxy.EntityType}).");
                    }
                }
                catch (Exception ex) { Logger.LogError($"[SRM] Error sending CreateEntity for {entityId} to client {clientId}: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
            }
            else { Logger.LogWarning($"[SRM] HandleEntityEnteredPvs: Proxy not found for Entity {entityId}. Might have just died."); }
        }

        private void HandleEntityLeftPvs(int clientId, int entityId)
        {
            if (_isDisposed) return;
            Logger.Log($"[SRM] Entity {entityId} left PVS for Client {clientId}. Sending VanishEntity.");
            try
            {
                // Context ID for VanishEntity is always the entityId itself
                _networkLayer.SendToClient(clientId, entityId, MessageType.VanishEntity, writer => { /* No payload */ });

                if (_replicatingEntityIds.Contains(entityId) && _activeEntityProxies.TryGetValue(entityId, out IServerProxy proxy))
                {
                    if (!_visibilityManager.GetClientsSeeingEntity(entityId).Any()) // Check if *no one* sees it anymore
                    {
                        proxy.StopReplicating();
                        _replicatingEntityIds.Remove(entityId);
                        Logger.Log($"[SRM] Stopped replicating events for entity {entityId} (Type: {proxy.EntityType}) (left all PVS).");
                    }
                }
            }
            catch (Exception ex) { Logger.LogError($"[SRM] Error processing EntityLeftPvs for {entityId} to client {clientId}: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        private void HandleClientMessage_GameLogic(int sourceNetworkId, int contextEntityId, MessageType messageType, BinaryReader reader)
        {
            if (_isDisposed) return;

            if (messageType == MessageType._ClientConnectRequest || messageType == MessageType._ClientDisconnect)
            {
                return; 
            }
            
            int gameClientId = sourceNetworkId; // Assuming direct mapping for game logic messages after connect

            if (!_clientConnections.TryGetValue(gameClientId, out ClientConnection clientConnection))
            {
                Logger.LogWarning($"[SRM] Received game logic message (Type: {messageType}) from unestablished/unknown GameClient ID: {gameClientId} (derived from SourceNetworkID: {sourceNetworkId}). Ignoring.");
                return;
            }
            // Ensure the client still has an active Escadre entity for commands that require it
            bool clientHasActiveEscadre = clientConnection.EscadreEntity != null && !clientConnection.EscadreEntity.IsDead;


            float currentTime = _serverClock.CurrentTime;
            bool success;

            switch (messageType)
            {
                case MessageType._ClientSyncState: // contextEntityId is the entity being synced
                    if (_activeEntityProxies.TryGetValue(contextEntityId, out IServerProxy proxy))
                    {
                        // Checksum and send correction state if needed
                        if (proxy.CheckClientSyncState(reader))
                        {
                            // Context ID for UpdateState is the entityId
                            _networkLayer.SendToClient(gameClientId, contextEntityId, MessageType.UpdateState, proxy.SerializeCorrectionState);
                        }
                    }
                    else { Logger.LogWarning($"[SRM CId={gameClientId}] _ClientSyncState for unknown EntityProxy ID: {contextEntityId}."); }
                    break;

                // Escadre General Commands (contextEntityId is 0, server uses clientConnection.EscadreEntity)
                case MessageType._SetCourse: 
                    if (!clientHasActiveEscadre) { Logger.LogWarning($"[SRM CId={gameClientId}] _SetCourse ignored, client has no active escadre."); break; }
                    try { clientConnection.RequestSetCourse(SerializationUtils.ReadVector2(reader), currentTime); } 
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _SetCourse: {ex.Message}"); } 
                    break;
                case MessageType._AttackEscadre: // Payload is targetEscadreEntityId
                    if (!clientHasActiveEscadre) { Logger.LogWarning($"[SRM CId={gameClientId}] _AttackEscadre ignored, client has no active escadre."); break; }
                    try { clientConnection.RequestAttackEscadre(reader.ReadInt32(), currentTime); } 
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _AttackEscadre: {ex.Message}"); } 
                    break;
                case MessageType._CancelAttack: 
                    if (!clientHasActiveEscadre) { Logger.LogWarning($"[SRM CId={gameClientId}] _CancelAttack ignored, client has no active escadre."); break; }
                    try { clientConnection.RequestCancelAttack(); } 
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _CancelAttack: {ex.Message}"); } 
                    break;
                
                // Shop & Formation Commands (contextEntityId is 0)
                case MessageType._RequestBuyShip:
                    if (!clientHasActiveEscadre) { Logger.LogWarning($"[SRM CId={gameClientId}] _RequestBuyShip ignored, client has no active escadre."); break; }
                    try
                    {
                        int shipDesignId = reader.ReadInt32();
                        Vector2 preferredOffset = SerializationUtils.ReadVector2(reader);
                        success = clientConnection.RequestBuyShip(shipDesignId, preferredOffset, currentTime);
                        if (!success) Logger.LogWarning($"[SRM CId={gameClientId}] _RequestBuyShip failed for design {shipDesignId}.");
                    }
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _RequestBuyShip: {ex.Message}"); }
                    break;
                case MessageType._RequestUpgradeShip:
                    if (!clientHasActiveEscadre) { Logger.LogWarning($"[SRM CId={gameClientId}] _RequestUpgradeShip ignored, client has no active escadre."); break; }
                    try
                    {
                        int shipEntityIdToUpgrade = reader.ReadInt32();
                        success = clientConnection.RequestUpgradeShip(shipEntityIdToUpgrade);
                        if (!success) Logger.LogWarning($"[SRM CId={gameClientId}] _RequestUpgradeShip failed for ship {shipEntityIdToUpgrade}.");
                    }
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _RequestUpgradeShip: {ex.Message}"); }
                    break;
                case MessageType._RequestSetFormation:
                    if (!clientHasActiveEscadre) { Logger.LogWarning($"[SRM CId={gameClientId}] _RequestSetFormation ignored, client has no active escadre."); break; }
                    try
                    {
                        int slotCount = reader.ReadInt32();
                        var newLayout = new List<Tuple<int, Vector2>>();
                        for(int i=0; i<slotCount; i++)
                        {
                            newLayout.Add(new Tuple<int, Vector2>(reader.ReadInt32(), SerializationUtils.ReadVector2(reader)));
                        }
                        success = clientConnection.RequestSetFormation(newLayout, currentTime);
                        if (!success) Logger.LogWarning($"[SRM CId={gameClientId}] _RequestSetFormation failed.");
                    }
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _RequestSetFormation: {ex.Message}"); }
                    break;

                default: 
                    Logger.LogWarning($"[SRM CId={gameClientId}] Received unhandled GameLogic MessageType ({messageType}) for Context {contextEntityId}."); 
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
            // if (_coreComposer != null) { _coreComposer.ClientRegisteredEvent -= HandleClientRegistered; _coreComposer.ClientUnregisteredEvent -= HandleClientUnregistered; } // REMOVED


            var entityProxyIds = new List<int>(_activeEntityProxies.Keys);
            foreach (var id in entityProxyIds) {
                if (_activeEntityProxies.TryGetValue(id, out IServerProxy proxy)) {
                    if (_replicatingEntityIds.Contains(id)) { proxy.StopReplicating(); }
                    if(_level.TryGetEntity(id, out Entity entity)) { 
                        entity.OnDeathEvent -= HandleModelEntityDeath; 
                    }
                }
                // _visibilityManager?.UnregisterEntity(id); // Unregistration from VM now happens on entity death or CoreComposer.Update
            }
            _replicatingEntityIds.Clear();
            _activeEntityProxies.Clear();

            // No _activeEscadreProxies to clear
            // _activeEscadreProxies.Clear(); // REMOVED

            Logger.Log("[ServerReplicationManager] Dispose complete.");
        }
    }
}