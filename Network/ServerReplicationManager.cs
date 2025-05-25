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
using Core.Ocean; 

namespace Core.Network
{
    public class ServerReplicationManager : IDisposable
    {
        private readonly Level _level;
        private readonly IServerNetworkLayer _networkLayer;
        private readonly VisibilityManager _visibilityManager;
        private readonly Dictionary<int, IServerProxy> _activeEntityProxies = new(); 
        private readonly HashSet<int> _replicatingEntityIds = new(); 
        private bool _isDisposed = false;
        private readonly Dictionary<int, ClientConnection> _clientConnections;
        private readonly IClock _serverClock;
        private readonly Dictionary<int, bool> _clientOceanDataSent = new Dictionary<int, bool>(); 

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
        
        public void OnClientSessionEstablished(int gameClientId)
        {
            if (!_clientOceanDataSent.ContainsKey(gameClientId) || !_clientOceanDataSent[gameClientId])
            {
                SendOceanInitializationData(gameClientId);
                _clientOceanDataSent[gameClientId] = true;
            }
        }
        
        private void SendOceanInitializationData(int gameClientId)
        {
            if (_level.OceanDataProvider == null || _level.OceanDataProvider.Settings == null)
            {
                Logger.LogWarning($"[SRM] Cannot send OceanInitializationData to client {gameClientId}: Server-side ocean data not configured.");
                // Send a message indicating no ocean, or client defaults to no ocean.
                // For now, just don't send if server isn't configured. Client will not have ocean.
                return;
            }

            Logger.Log($"[SRM] Sending OceanInitializationData (Settings only) to Client {gameClientId}.");
            _networkLayer.SendToClient(gameClientId, 0, MessageType.OceanInitializationData, writer =>
            {
                SerializationUtils.WriteOceanSettings(writer, _level.OceanDataProvider.Settings);
                
                // This is where the 768KB texture data would be sent if we were doing that.
                // For now, client will have to use a placeholder or assume a pre-shared texture.
                // We send a boolean to indicate if texture data *would* follow.
                writer.Write(false); // false = no raw texture data block follows in this message.
            });
        }


        private void HandleEntityAddedToLevel(Entity entity)
        {
            if (_isDisposed || entity.IsDead || _activeEntityProxies.ContainsKey(entity.Id)) return;
            Logger.Log($"[SRM] Entity Added to Level: ID={entity.Id}, Type={entity.EntityType}. Creating Proxy.");
            try
            {
                IServerProxy proxy = ServerProxyFactory.CreateServerProxy(entity, _networkLayer);
                _activeEntityProxies.Add(entity.Id, proxy);
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

            if (!_clientOceanDataSent.ContainsKey(clientId) || !_clientOceanDataSent[clientId])
            {
                if (_clientConnections.TryGetValue(clientId, out var clientConn) &&
                    clientConn.EscadreEntity != null && clientConn.EscadreEntity.Id == entityId)
                {
                    OnClientSessionEstablished(clientId); 
                }
            }
            
            if (_activeEntityProxies.TryGetValue(entityId, out IServerProxy proxy))
            {
                if (_level.TryGetEntity(entityId, out Entity ent) && ent.IsDead)
                {
                    Logger.LogWarning($"[SRM] Entity {entityId} entered PVS for Client {clientId}, but is already dead in Level. Not sending CreateEntity.");
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
                _networkLayer.SendToClient(clientId, entityId, MessageType.VanishEntity, writer => { /* No payload */ });

                if (_replicatingEntityIds.Contains(entityId) && _activeEntityProxies.TryGetValue(entityId, out IServerProxy proxy))
                {
                    if (!_visibilityManager.GetClientsSeeingEntity(entityId).Any()) 
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
            
            int gameClientId = sourceNetworkId; 

            if (!_clientConnections.TryGetValue(gameClientId, out ClientConnection clientConnection))
            {
                Logger.LogWarning($"[SRM] Received game logic message (Type: {messageType}) from unestablished/unknown GameClient ID: {gameClientId} (derived from SourceNetworkID: {sourceNetworkId}). Ignoring.");
                return;
            }
            bool clientHasActiveEscadre = clientConnection.EscadreEntity != null && !clientConnection.EscadreEntity.IsDead;


            float currentTime = _serverClock.CurrentTime;
            bool success;

            switch (messageType)
            {
                case MessageType._ClientSyncState: 
                    if (_activeEntityProxies.TryGetValue(contextEntityId, out IServerProxy proxy))
                    {
                        if (proxy.CheckClientSyncState(reader))
                        {
                            _networkLayer.SendToClient(gameClientId, contextEntityId, MessageType.UpdateState, proxy.SerializeCorrectionState);
                        }
                    }
                    else { Logger.LogWarning($"[SRM CId={gameClientId}] _ClientSyncState for unknown EntityProxy ID: {contextEntityId}."); }
                    break;
                case MessageType._SetCourse: 
                    if (!clientHasActiveEscadre) { Logger.LogWarning($"[SRM CId={gameClientId}] _SetCourse ignored, client has no active escadre."); break; }
                    try { clientConnection.RequestSetCourse(SerializationUtils.ReadVector2(reader), currentTime); } 
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _SetCourse: {ex.Message}"); } 
                    break;
                case MessageType._AttackEscadre: 
                    if (!clientHasActiveEscadre) { Logger.LogWarning($"[SRM CId={gameClientId}] _AttackEscadre ignored, client has no active escadre."); break; }
                    try { clientConnection.RequestAttackEscadre(reader.ReadInt32(), currentTime); } 
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _AttackEscadre: {ex.Message}"); } 
                    break;
                case MessageType._CancelAttack: 
                    if (!clientHasActiveEscadre) { Logger.LogWarning($"[SRM CId={gameClientId}] _CancelAttack ignored, client has no active escadre."); break; }
                    try { clientConnection.RequestCancelAttack(); } 
                    catch (Exception ex) { Logger.LogError($"[SRM CId={gameClientId}] Error processing _CancelAttack: {ex.Message}"); } 
                    break;
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

            var entityProxyIds = new List<int>(_activeEntityProxies.Keys);
            foreach (var id in entityProxyIds) {
                if (_activeEntityProxies.TryGetValue(id, out IServerProxy proxy)) {
                    if (_replicatingEntityIds.Contains(id)) { proxy.StopReplicating(); }
                    if(_level.TryGetEntity(id, out Entity entity)) { 
                        entity.OnDeathEvent -= HandleModelEntityDeath; 
                    }
                }
            }
            _replicatingEntityIds.Clear();
            _activeEntityProxies.Clear();
            _clientOceanDataSent.Clear();
            Logger.Log("[ServerReplicationManager] Dispose complete.");
        }
    }
}