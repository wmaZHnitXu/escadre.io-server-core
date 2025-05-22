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

        private bool _isDisposed = false;
        private readonly Dictionary<int, ClientConnection> _clientConnections = new Dictionary<int, ClientConnection>();
        public IReadOnlyDictionary<int, ClientConnection> ClientConnections => _clientConnections;
        
        private readonly Dictionary<int, int> _sourceNetworkIdToGameClientIdMap = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _gameClientIdToSourceNetworkIdMap = new Dictionary<int, int>();


        public event Action<ClientConnection> ClientRegisteredEvent; // Still useful for systems that need to know when a client is fully set up
        public event Action<ClientConnection> ClientUnregisteredEvent;

        public CoreComposer(IServerNetworkLayer networkLayer, 
                            IVisibilityStrategy visibilityStrategy, 
                            IClock serverClock,
                            IClientConnectionValidator connectionValidator)
        {
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _visibilityStrategy = visibilityStrategy ?? throw new ArgumentNullException(nameof(visibilityStrategy));
            _serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock)); 
            _connectionValidator = connectionValidator ?? throw new ArgumentNullException(nameof(connectionValidator));

            Logger.Log("[CoreComposer] Initializing Core Systems...");

            ServerLevel = new Level(); 
            VisibilityManager = new VisibilityManager(_visibilityStrategy);
            // Pass 'this' (CoreComposer) to SRM so it can subscribe to ClientRegistered/Unregistered events
            // This is if SRM needs to do specific setup for EscadreProxy *entities* when a client connects,
            // but now Escadre is a generic entity, so specific SRM handling for it might be less.
            // For now, SRM doesn't need CoreComposer reference for EscadreProxy creation as it's handled by generic entity flow.
            _replicationManager = new ServerReplicationManager(ServerLevel, _networkLayer, VisibilityManager, _clientConnections, _serverClock); 
            
            _networkLayer.OnClientMessageReceived += HandleNetworkMessage_SessionManagement;

            // Subscribe to entity removal to handle Escadre destruction
            ServerLevel.OnEntityRemovedEvent += HandleEntityRemoved; 

            Logger.Log("[CoreComposer] Initialization Complete. Listening for client connections.");
        }

        private void HandleEntityRemoved(Entity entity)
        {
            if (_isDisposed) return;

            if (entity is Escadre destroyedEscadre)
            {
                Logger.Log($"[CoreComposer] Detected removal of Escadre Entity ID {destroyedEscadre.Id}, Owner: {destroyedEscadre.OwnerClientId}. Checking for associated client connection.");
                if (_clientConnections.TryGetValue(destroyedEscadre.OwnerClientId, out ClientConnection clientConnection))
                {
                    // Check if the client is still considered active with this escadre
                    if (clientConnection.EscadreEntity == destroyedEscadre && clientConnection.CurrentState != ClientState.Destroyed)
                    {
                        Logger.Log($"[CoreComposer] Escadre {destroyedEscadre.Id} for Client {clientConnection.ClientId} was removed/destroyed. Setting ClientConnection state to Destroyed.");
                        // It's important this SetState to Destroyed happens *before* any UnregisterClient call for this client,
                        // or that UnregisterClient doesn't try to re-kill the already dead escadre.
                        // The Escadre is already dead and removed from the level.
                        // We just need to update the ClientConnection's state.
                        // UnregisterClient also handles PVS removal etc., which might be desired.
                        // For now, let's just set the state. If full unregistration is needed, that's a different path.
                        clientConnection.SetState(ClientState.Destroyed);
                        // Consider if full UnregisterClient(clientConnection.ClientId) should be called here.
                        // If so, ensure UnregisterClient handles a null/already-dead EscadreEntity gracefully.
                        // For now, simply setting state to Destroyed might be enough to stop further interactions.
                        // UnregisterClient also removes the client from _clientConnections, which might be too much if they could e.g. respawn.
                        // If a client whose escadre is destroyed should be fully disconnected, then call UnregisterClient.
                        // Let's assume for now that their connection persists but is marked as Destroyed.
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
                    // This could happen if an Escadre is destroyed for a client that already disconnected
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
                        if(_clientConnections.ContainsKey(oldGameId)) UnregisterClient(oldGameId, true);  // Pass a flag to indicate it's a forceful unregister due to new connection
                        else { _gameClientIdToSourceNetworkIdMap.Remove(oldGameId); _sourceNetworkIdToGameClientIdMap.Remove(sourceNetworkId); } 
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
            VisibilityManager.AddOrUpdateClientView(clientConnection); // Client view now centered on Escadre entity

            // Create Escadre as an Entity
            Escadre escadreEntity = new Escadre(ServerLevel, identity.ClientId, identity.Nickname, initialSpawnPosition);
            // ServerLevel.AddEntity(escadreEntity); // Already done by Escadre constructor
            
            // Escadre entity creation will be picked up by ServerReplicationManager via Level.OnEntityAddedEvent
            // and then replicated to clients (including the owner).

            clientConnection.AssignEscadre(escadreEntity); // ClientConnection now holds reference to Escadre Entity
            
            // Add initial ship to this escadre
            Ship initialShip = new DefaultShip(ServerLevel, escadreEntity, initialSpawnPosition); // Ship takes Escadre entity
            // escadreEntity.AddShip(initialShip); // AddShip is internal, called by Shop or initial setup.
                                               // Here, directly add. Note: ID might not be set yet by Level if _toAdd not processed.
                                               // Let's assume initialShip's constructor adds it to level, and Level assigns ID before AddShip is called by shop.
                                               // For initial setup, let's ensure ship is added to Level first, then to escadre.
                                               // The DefaultShip constructor already calls Level.AddEntity.
                                               // We need to ensure its ID is assigned *before* AddShip if formation relies on it immediately.
                                               // For now, let AddShip handle adding it to the formation with auto-slotting.
            escadreEntity.AddShip(initialShip);
            escadreEntity.UpdateShipMovementTargets(_serverClock.CurrentTime);

            Logger.Log($"[CoreComposer] Client Session Created & Registered: ID={identity.ClientId}, Nick='{identity.Nickname}'. Mapped to NetworkSourceID: {sourceNetworkId}. Escadre Entity ID: {escadreEntity.Id}");

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


                VisibilityManager.RemoveClientView(gameClientId); // PVS source is Escadre, which will be destroyed
                
                if (clientConnection.EscadreEntity != null)
                {
                    Logger.Log($"[CoreComposer] Killing Escadre Entity ID {clientConnection.EscadreEntity.Id} for client {gameClientId}.");
                    clientConnection.EscadreEntity.Kill(true); // Kill the Escadre entity silently
                    // ServerLevel.RemoveEntity(clientConnection.EscadreEntity) will be handled by Level's death processing
                }
                clientConnection.ClearEscadreReference(); // Clears reference in ClientConnection
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
                // Register all entities (including Escadres) with VisibilityManager
                foreach (var entity in ServerLevel.GetAllEntities()) 
                {
                    if (!entity.IsDead) // Only register living entities for visibility calculation
                    {
                        VisibilityManager.RegisterEntity(entity); 
                    }
                }
                // Update client views - PVS is now centered on their Escadre entity's position
                foreach (var clientConn in _clientConnections.Values.ToList()) 
                {
                    if (clientConn.CurrentState != ClientState.Destroyed && clientConn.EscadreEntity != null && !clientConn.EscadreEntity.IsDead) 
                    {
                        // ClientConnection.Position property now correctly reflects EscadreEntity.Position
                        VisibilityManager.AddOrUpdateClientView(clientConn);
                    }
                    else if (clientConn.CurrentState != ClientState.Destroyed && (clientConn.EscadreEntity == null || clientConn.EscadreEntity.IsDead))
                    {
                        // If escadre is gone but client still connected (e.g. spectating), PVS might be static or based on spectator cam
                        // For now, if escadre is dead, client view might become invalid for PVS until re-spawn or proper spectator.
                        // VisibilityManager.RemoveClientView(clientConn.ClientId); // Or update to a spectator view
                    }
                }
                VisibilityManager.UpdateAllClientVisibility();
            }
            catch (Exception ex) { Logger.LogError($"[CoreComposer] Error during Update loop: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        public void Dispose()
        {
            if (_isDisposed) return; _isDisposed = true;
            Logger.Log("[CoreComposer] Disposing...");

            // Unsubscribe from ServerLevel events
            if (ServerLevel != null)
            {
                ServerLevel.OnEntityRemovedEvent -= HandleEntityRemoved;
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
            VisibilityManager?.Dispose(); // VisibilityManager.UnregisterEntity will be called for Escadres
            ServerLevel?.Destroy(); // This will kill all entities, including Escadres
            _visibilityStrategy?.Dispose();

            ClientRegisteredEvent = null;
            ClientUnregisteredEvent = null;

            Logger.Log("[CoreComposer] Dispose complete.");
        }
    }
}