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
        
        // For MockNetworkLayer to map sourceNetworkId to gameClientId for disconnects
        // In a real system, the network layer (e.g. SignalR hub) would provide this mapping via its connection context.
        private readonly Dictionary<int, int> _sourceNetworkIdToGameClientIdMap = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _gameClientIdToSourceNetworkIdMap = new Dictionary<int, int>();


        public event Action<ClientConnection> ClientRegisteredEvent;
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
            _replicationManager = new ServerReplicationManager(ServerLevel, _networkLayer, VisibilityManager, _clientConnections, _serverClock);
            
            _networkLayer.OnClientMessageReceived += HandleNetworkMessage_SessionManagement;

            Logger.Log("[CoreComposer] Initialization Complete. Listening for client connections.");
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
                        Logger.LogWarning($"[CoreComposer] Client ID {validatedIdentity.ClientId} (from token) is already connected. Current policy: Rejecting new connection for this ID from source {sourceNetworkId}.");
                        return;
                    }
                    if (_sourceNetworkIdToGameClientIdMap.ContainsKey(sourceNetworkId))
                    {
                        Logger.LogWarning($"[CoreComposer] Network Source ID {sourceNetworkId} is already associated with game client {_sourceNetworkIdToGameClientIdMap[sourceNetworkId]}. New connect request from same source for different/new identity {validatedIdentity.ClientId} - this might indicate a client bug or reconnect attempt not fully handled. Overwriting mapping for now.");
                        // Clean up old mapping if sourceNetworkId is being reused by a "new" client identity
                        int oldGameId = _sourceNetworkIdToGameClientIdMap[sourceNetworkId];
                        _gameClientIdToSourceNetworkIdMap.Remove(oldGameId);
                    }


                    Logger.Log($"[CoreComposer] Token validated for client. JWT ClientID: {validatedIdentity.ClientId}, Nick: '{validatedIdentity.Nickname}', Admin: {validatedIdentity.IsAdmin}, AuthType: {validatedIdentity.AuthType}. Proceeding to register client session.");
                    
                    float pvsRadius = 150f; 
                    if (validatedIdentity.IsAdmin) pvsRadius = 300f; 

                    float x = (validatedIdentity.ClientId % 7) * 15f - 45f; 
                    float z = ((validatedIdentity.ClientId / 7) % 7) * 15f - 45f;
                    Vector3 spawnPosition = new Vector3(x, 0, z);
                    
                    CreateClientSessionAndEscadre(validatedIdentity, pvsRadius, spawnPosition, sourceNetworkId);
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

        private ClientConnection CreateClientSessionAndEscadre(ClientIdentity identity, float initialPvsRadius, Vector3 initialSpawnPosition, int sourceNetworkId)
        {
            if (_isDisposed) { Logger.LogWarning("[CoreComposer] Attempted to create session on disposed composer."); return null; }
            if (_clientConnections.ContainsKey(identity.ClientId)) { 
                Logger.LogError($"[CoreComposer] CRITICAL: Attempt to create session for already existing ClientID {identity.ClientId} in CreateClientSessionAndEscadre."); 
                return _clientConnections[identity.ClientId]; 
            }

            // Map sourceNetworkId to gameClientId for session management
            _sourceNetworkIdToGameClientIdMap[sourceNetworkId] = identity.ClientId;
            _gameClientIdToSourceNetworkIdMap[identity.ClientId] = sourceNetworkId;


            var clientConnection = new ClientConnection(identity.ClientId, initialPvsRadius); 
            _clientConnections.Add(identity.ClientId, clientConnection); 
            VisibilityManager.AddOrUpdateClientView(clientConnection);

            Escadre escadre = new Escadre(identity.ClientId, ServerLevel);
            if (!ServerLevel.AddEscadre(escadre))
            {
                Logger.LogError($"[CoreComposer] Failed to add escadre for client {identity.ClientId} to Level. Cleaning up partially created session.");
                VisibilityManager.RemoveClientView(identity.ClientId); 
                _clientConnections.Remove(identity.ClientId); 
                _sourceNetworkIdToGameClientIdMap.Remove(sourceNetworkId);
                _gameClientIdToSourceNetworkIdMap.Remove(identity.ClientId);
                return null;
            }
            clientConnection.AssignEscadre(escadre);
            
            Ship initialShip = new DefaultShip(ServerLevel, escadre, initialSpawnPosition);
            escadre.AddShip(initialShip); 
            if (escadre.CurrentDestination.HasValue)
            {
                initialShip.SetMovementTarget(escadre.CurrentDestination.Value, _serverClock.CurrentTime);
            }

            Logger.Log($"[CoreComposer] Client Session Created & Registered: ID={identity.ClientId}, Nick='{identity.Nickname}', Admin={identity.IsAdmin}, AuthType={identity.AuthType}. Mapped to NetworkSourceID: {sourceNetworkId}.");

            ClientRegisteredEvent?.Invoke(clientConnection); 
            
            if (_networkLayer is MockNetworkLayer mockLayer)
            {
                // This call tells the mock layer how to route S->C messages for this gameClientId
                mockLayer.MapNetworkSourceToClientId(sourceNetworkId, identity.ClientId);
            }

            return clientConnection;
        }

        public void UnregisterClient(int gameClientId) 
        {
            if (_isDisposed) return;
            if (_clientConnections.TryGetValue(gameClientId, out ClientConnection clientConnection))
            {
                Logger.Log($"[CoreComposer] Unregistering Game Client ID {gameClientId}.");
                ClientConnection tempConnectionReference = clientConnection; 

                // Clean up sourceNetworkId mapping
                if (_gameClientIdToSourceNetworkIdMap.TryGetValue(gameClientId, out int sourceNetworkId))
                {
                    _sourceNetworkIdToGameClientIdMap.Remove(sourceNetworkId);
                    _gameClientIdToSourceNetworkIdMap.Remove(gameClientId);
                    Logger.Log($"[CoreComposer] Removed mapping for Game Client ID {gameClientId} (was Network Source ID {sourceNetworkId}).");
                    
                    // Also inform MockNetworkLayer if it's being used, so it can clear its internal routing for the source ID
                    if (_networkLayer is MockNetworkLayer mockLayer)
                    {
                        mockLayer.RemoveNetworkSourceMappingForClientId(gameClientId); // This might be redundant if MapNetworkSourceToClientId implies registration
                        // mockLayer.UnregisterMockClientS2CRouting(sourceNetworkId); // This should be called by ClientComposer's OnDisable
                    }
                }
                else { Logger.LogWarning($"[CoreComposer] No sourceNetworkId mapping found for Game Client ID {gameClientId} during unregistration."); }


                VisibilityManager.RemoveClientView(gameClientId);
                if (clientConnection.EscadreInstance != null)
                {
                    clientConnection.EscadreInstance.Disband(true); 
                    ServerLevel.RemoveEscadre(clientConnection.EscadreInstance.OwnerClientId);
                }
                clientConnection.ClearEscadreReference(); 
                clientConnection.SetState(ClientState.Destroyed); 
                _clientConnections.Remove(gameClientId);

                ClientUnregisteredEvent?.Invoke(tempConnectionReference); 
            } else { Logger.LogWarning($"[CoreComposer] Attempted to unregister unknown client: {gameClientId}"); }
        }

        public void Update(float deltaTime)
        {
            if (_isDisposed) return;
            try
            {
                ServerLevel.DoUpdate(deltaTime);
                foreach (var entity in ServerLevel.GetAllEntities())
                {
                    VisibilityManager.RegisterEntity(entity); 
                }
                foreach (var clientConn in _clientConnections.Values.ToList()) 
                {
                    if (clientConn.CurrentState != ClientState.Destroyed) 
                    {
                        VisibilityManager.AddOrUpdateClientView(clientConn);
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
            _visibilityStrategy?.Dispose();

            ClientRegisteredEvent = null;
            ClientUnregisteredEvent = null;

            Logger.Log("[CoreComposer] Dispose complete.");
        }
    }
}