// File: Core/CoreComposer.cs
using System;
using System.Collections.Generic;
using System.IO; 
using Core.Model;
using Core.Network;
using Core.Logging;
using Core.Visibility;
using Core.Session;
using Core.Primitives;
using Core.Time;
using System.Linq;
using Core.Ocean; 


namespace Core
{
    public class CoreComposer : IDisposable
    {
        public Level ServerLevel { get; }
        public VisibilityManager VisibilityManager { get; }
        private readonly ServerReplicationManager _replicationManager;
        private readonly IVisibilityStrategy _visibilityStrategy; 
        private readonly IClock _serverClock; 
        private readonly IServerNetworkLayer _networkLayer; 
        private readonly IClientConnectionValidator _connectionValidator; 
        private readonly IOceanDataProvider _serverOceanDataProvider; 

        private bool _isDisposed = false;
        private readonly Dictionary<int, ClientConnection> _clientConnections = new Dictionary<int, ClientConnection>();
        public IReadOnlyDictionary<int, ClientConnection> ClientConnections => _clientConnections;
        
        private readonly Dictionary<int, int> _sourceNetworkIdToGameClientIdMap = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _gameClientIdToSourceNetworkIdMap = new Dictionary<int, int>();


        public event Action<ClientConnection> ClientRegisteredEvent; 
        public event Action<ClientConnection> ClientUnregisteredEvent;

        public CoreComposer(IServerNetworkLayer networkLayer, 
                            IVisibilityStrategy visibilityStrategyAndSpatialIndex, 
                            IClock serverClock,
                            IClientConnectionValidator connectionValidator,
                            IOceanDataProvider serverOceanDataProvider) 
        {
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _visibilityStrategy = visibilityStrategyAndSpatialIndex ?? throw new ArgumentNullException(nameof(visibilityStrategyAndSpatialIndex));
            _serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock)); 
            _connectionValidator = connectionValidator ?? throw new ArgumentNullException(nameof(connectionValidator));
            _serverOceanDataProvider = serverOceanDataProvider; 

            Logger.Log("[CoreComposer] Initializing Core Systems...");

            ServerLevel = new Level(_serverOceanDataProvider, _visibilityStrategy); 
            VisibilityManager = new VisibilityManager(_visibilityStrategy, _serverClock); // Pass clock for throttling
            _replicationManager = new ServerReplicationManager(ServerLevel, _networkLayer, VisibilityManager, _clientConnections, _serverClock); 
            
            _networkLayer.OnClientMessageReceived += HandleNetworkMessage_SessionManagement;
            // Subscribe VM to Level's entity events for robust registration/unregistration
            ServerLevel.OnEntityAddedEvent += VisibilityManager.RegisterEntity;
            ServerLevel.OnEntityRemovedEvent += HandleEntityRemovedFromLevelAndVisibilityManager; 

            Logger.Log("[CoreComposer] Initialization Complete. Listening for client connections.");
        }

        private void HandleEntityRemovedFromLevelAndVisibilityManager(Entity entity)
        {
            if (_isDisposed) return;
            // VisibilityManager is now directly subscribed to OnEntityRemovedEvent from Level
            // so it will call its UnregisterEntity method. We only need to handle
            // CoreComposer specific logic here (Escadre cleanup).

            VisibilityManager.UnregisterEntity(entity.Id); // Explicitly ensure VM knows

            if (entity is Escadre destroyedEscadre)
            {
                Logger.Log($"[CoreComposer] Detected removal of Escadre Entity ID {destroyedEscadre.Id}, Owner: {destroyedEscadre.OwnerClientId}. Checking for associated client connection.");
                if (_clientConnections.TryGetValue(destroyedEscadre.OwnerClientId, out ClientConnection clientConnection))
                {
                    if (clientConnection.EscadreEntity == destroyedEscadre && clientConnection.CurrentState != ClientState.Destroyed)
                    {
                        Logger.Log($"[CoreComposer] Escadre {destroyedEscadre.Id} for Client {clientConnection.ClientId} was removed/destroyed. Setting ClientConnection state to Destroyed.");
                        clientConnection.SetState(ClientState.Destroyed);
                    }
                    else if (clientConnection.CurrentState == ClientState.Destroyed)
                    {
                        Logger.Log($"[CoreComposer] Escadre {destroyedEscadre.Id} for Client {clientConnection.ClientId} was removed, but client already in Destroyed state.");
                    }
                    else if (clientConnection.EscadreEntity != destroyedEscadre)
                    {
                         Logger.LogWarning($"[CoreComposer] Escadre {destroyedEscadre.Id} removed, but Client {clientConnection.ClientId} is associated with a different Escadre Entity ({clientConnection.EscadreEntity?.Id}). No state change for client.");
                    }
                }
                else
                {
                    Logger.Log($"[CoreComposer] Escadre Entity ID {destroyedEscadre.Id} (Owner: {destroyedEscadre.OwnerClientId}) removed, but no active client connection found for this owner.");
                }
            }
        }

        private void HandleNetworkMessage_SessionManagement(int sourceNetworkId, int entityIdContext, MessageType messageType, BinaryReader reader)
        {
            if (_isDisposed) return;

            if (messageType == MessageType._ClientConnectRequest)
            {
                Logger.Log($"[CoreComposer] Received _ClientConnectRequest from network source ID {sourceNetworkId}.");
                ClientIdentity? identity = _connectionValidator.ValidateTokenAndGetIdentity(reader);

                if (identity.HasValue)
                {
                    ClientIdentity validatedIdentity = identity.Value;
                    if (_clientConnections.ContainsKey(validatedIdentity.ClientId))
                    {
                        Logger.LogWarning($"[CoreComposer] Client ID {validatedIdentity.ClientId} (from token) is already connected. Rejecting new connection from source {sourceNetworkId}.");
                        return;
                    }
                    if (_sourceNetworkIdToGameClientIdMap.ContainsKey(sourceNetworkId))
                    {
                        Logger.LogWarning($"[CoreComposer] Network Source ID {sourceNetworkId} is already associated with game client {_sourceNetworkIdToGameClientIdMap[sourceNetworkId]}. New connect request from same source for different/new identity {validatedIdentity.ClientId} - this might indicate a client bug or reconnect attempt not fully handled. Overwriting mapping for now.");
                        int oldGameId = _sourceNetworkIdToGameClientIdMap[sourceNetworkId];
                        if(_clientConnections.ContainsKey(oldGameId)) UnregisterClient(oldGameId, true);  
                        else { 
                            _gameClientIdToSourceNetworkIdMap.Remove(oldGameId); 
                            // _sourceNetworkIdToGameClientIdMap.Remove(sourceNetworkId); // This will be overwritten
                        } 
                    }

                    Logger.Log($"[CoreComposer] Token validated for client. JWT ClientID: {validatedIdentity.ClientId}, Nick: '{validatedIdentity.Nickname}', Admin: {validatedIdentity.IsAdmin}, AuthType: {validatedIdentity.AuthType}. Proceeding to register client session.");
                    
                    float pvsRadius = 150f; 
                    if (validatedIdentity.IsAdmin) pvsRadius = 300f; 

                    float x = (validatedIdentity.ClientId % 7) * 25f - 75f; 
                    float z = ((validatedIdentity.ClientId / 7) % 7) * 25f - 75f;
                    Vector3 spawnPosition = new Vector3(x, 0, z); 
                    
                    CreateClientSessionAndEscadreEntity(validatedIdentity, pvsRadius, spawnPosition, sourceNetworkId);
                }
                else
                {
                    Logger.LogWarning($"[CoreComposer] Connection validation failed for request from network source ID {sourceNetworkId}. Token invalid or client not authorized.");
                }
            }
            else if (messageType == MessageType._ClientDisconnect)
            {
                Logger.Log($"[CoreComposer] Received _ClientDisconnect from network source ID {sourceNetworkId}.");
                if (_sourceNetworkIdToGameClientIdMap.TryGetValue(sourceNetworkId, out int gameClientIdToDisconnect))
                {
                    Logger.Log($"[CoreComposer] Game Client ID {gameClientIdToDisconnect} associated with source {sourceNetworkId} will be unregistered due to disconnect request.");
                    UnregisterClient(gameClientIdToDisconnect);
                }
                else
                {
                    Logger.LogWarning($"[CoreComposer] Received _ClientDisconnect from unknown/unmapped network source ID {sourceNetworkId}. No action taken.");
                }
            }
        }

        private ClientConnection CreateClientSessionAndEscadreEntity(ClientIdentity identity, float initialPvsRadius, Vector3 initialSpawnPosition, int sourceNetworkId)
        {
            if (_isDisposed) { Logger.LogWarning("[CoreComposer] Attempted to create session on disposed composer."); return null; }
            if (_clientConnections.ContainsKey(identity.ClientId)) { 
                Logger.LogError($"[CoreComposer] CRITICAL: Attempt to create session for already existing ClientID {identity.ClientId} in CreateClientSessionAndEscadreEntity."); 
                return _clientConnections[identity.ClientId]; 
            }

            _sourceNetworkIdToGameClientIdMap[sourceNetworkId] = identity.ClientId;
            _gameClientIdToSourceNetworkIdMap[identity.ClientId] = sourceNetworkId;
            
            if (_networkLayer is MockNetworkLayer mockLayer)
            {
                mockLayer.MapNetworkSourceToClientId(sourceNetworkId, identity.ClientId);
                Logger.Log($"[CoreComposer] MockNetworkLayer mapping established for GameClient {identity.ClientId} <-> NetworkSource {sourceNetworkId}.");
            }

            var clientConnection = new ClientConnection(identity.ClientId, ServerLevel, initialPvsRadius); 
            _clientConnections.Add(identity.ClientId, clientConnection); 
            VisibilityManager.AddOrUpdateClientView(clientConnection); 

            Escadre escadreEntity = new Escadre(ServerLevel, identity.ClientId, identity.Nickname, initialSpawnPosition);
            clientConnection.AssignEscadre(escadreEntity); 
            
            Ship initialShip = new DefaultShip(ServerLevel, escadreEntity, initialSpawnPosition); 
            escadreEntity.AddShip(initialShip);
            escadreEntity.UpdateShipMovementTargets(_serverClock.CurrentTime);

            Logger.Log($"[CoreComposer] Client Session Created & Registered: ID={identity.ClientId}, Nick='{identity.Nickname}'. Mapped to NetworkSourceID: {sourceNetworkId}. Escadre Entity ID: {escadreEntity.Id}");

            _replicationManager.OnClientSessionEstablished(identity.ClientId);
            ClientRegisteredEvent?.Invoke(clientConnection); 
            
            return clientConnection;
        }

        public void UnregisterClient(int gameClientId, bool dueToOverwrite = false) 
        {
            if (_isDisposed) return;
            if (_clientConnections.TryGetValue(gameClientId, out ClientConnection clientConnection))
            {
                Logger.Log($"[CoreComposer] Unregistering Game Client ID {gameClientId}. Due to overwrite: {dueToOverwrite}");
                ClientConnection tempConnectionReference = clientConnection; 

                ClientUnregisteredEvent?.Invoke(tempConnectionReference); 

                if (_gameClientIdToSourceNetworkIdMap.TryGetValue(gameClientId, out int sourceNetworkId))
                {
                    _sourceNetworkIdToGameClientIdMap.Remove(sourceNetworkId);
                    _gameClientIdToSourceNetworkIdMap.Remove(gameClientId);
                    Logger.Log($"[CoreComposer] Removed mapping for Game Client ID {gameClientId} (was Network Source ID {sourceNetworkId}).");
                    
                    if (_networkLayer is MockNetworkLayer mockLayer)
                    {
                        mockLayer.RemoveNetworkSourceMappingForClientId(gameClientId); 
                    }
                }
                else { Logger.LogWarning($"[CoreComposer] No sourceNetworkId mapping found for Game Client ID {gameClientId} during unregistration."); }

                VisibilityManager.RemoveClientView(gameClientId); 
                
                if (clientConnection.EscadreEntity != null)
                {
                    Logger.Log($"[CoreComposer] Killing Escadre Entity ID {clientConnection.EscadreEntity.Id} for client {gameClientId}.");
                    clientConnection.EscadreEntity.Kill(true); 
                }
                clientConnection.ClearEscadreReference(); 
                clientConnection.SetState(ClientState.Destroyed); 
                _clientConnections.Remove(gameClientId);
            } else { Logger.LogWarning($"[CoreComposer] Attempted to unregister unknown client: {gameClientId}"); }
        }

        public void Update(float deltaTime)
        {
            if (_isDisposed) return;
            try
            {
                ServerLevel.DoUpdate(deltaTime); 
                
                // Entity registration/unregistration with VisibilityManager and its strategy
                // is now handled by Level.OnEntityAddedEvent and Level.OnEntityRemovedEvent,
                // and entity position updates in strategy are handled by Level.DoUpdate().
                // No need for a general loop here to call VisibilityManager.RegisterEntity for all entities.

                // Client views are updated if their PVS center (Escadre position) moves.
                // VisibilityManager will use this for PVS calculations.
                foreach (var clientConn in _clientConnections.Values.ToList()) 
                {
                    if (clientConn.CurrentState != ClientState.Destroyed && clientConn.EscadreEntity != null && !clientConn.EscadreEntity.IsDead) 
                    {
                        // Inform VM about client view changes. VM's internal throttling
                        // will decide if a PVS recalc is needed.
                        VisibilityManager.AddOrUpdateClientView(clientConn);
                    }
                }
                VisibilityManager.UpdateAllClientVisibility(); // This now incorporates throttling
            }
            catch (Exception ex) { Logger.LogError($"[CoreComposer] Error during Update loop: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        public void Dispose()
        {
            if (_isDisposed) return; _isDisposed = true;
            Logger.Log("[CoreComposer] Disposing...");

            if (ServerLevel != null)
            {
                ServerLevel.OnEntityAddedEvent -= VisibilityManager.RegisterEntity;
                ServerLevel.OnEntityRemovedEvent -= HandleEntityRemovedFromLevelAndVisibilityManager;
            }

            if (_networkLayer != null)
            {
                _networkLayer.OnClientMessageReceived -= HandleNetworkMessage_SessionManagement;
            }

            var clientIds = new List<int>(_clientConnections.Keys);
            foreach (var clientId in clientIds) { UnregisterClient(clientId); } 
            _clientConnections.Clear();
            _sourceNetworkIdToGameClientIdMap.Clear();
            _gameClientIdToSourceNetworkIdMap.Clear();

            _replicationManager?.Dispose(); 
            VisibilityManager?.Dispose(); 
            ServerLevel?.Destroy(); 
            
            // The _visibilityStrategy is used by both ServerLevel and VisibilityManager.
            // It should be disposed once, typically by the owner who created it.
            // If CoreComposer created it (as it does now), it should dispose it.
            // However, VisibilityManager.Dispose() might also dispose its strategy if it assumes ownership.
            // Let's ensure single disposal: if VM disposes it, CoreComposer shouldn't.
            // For now, assume VM's Dispose handles the strategy it was given.
            // If _visibilityStrategy implemented IDisposable and VM.Dispose didn't call it, do it here:
            // (_visibilityStrategy as IDisposable)?.Dispose(); 

            //_serverOceanDataProvider?.Dispose();

            ClientRegisteredEvent = null;
            ClientUnregisteredEvent = null;

            Logger.Log("[CoreComposer] Dispose complete.");
        }
    }
}