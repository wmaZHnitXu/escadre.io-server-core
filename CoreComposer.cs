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
            Logger.Log("[CoreComposer] Level created.");

            VisibilityManager = new VisibilityManager(_visibilityStrategy);
            Logger.Log("[CoreComposer] VisibilityManager created.");

            _replicationManager = new ServerReplicationManager(ServerLevel, _networkLayer, VisibilityManager, _clientConnections, _serverClock);
            Logger.Log("[CoreComposer] ServerReplicationManager created.");
            
            _networkLayer.OnClientMessageReceived += HandleNetworkMessage_SessionManagement;

            Logger.Log("[CoreComposer] Initialization Complete. Listening for client connections.");
        }

        private void HandleNetworkMessage_SessionManagement(int sourceNetworkId, int entityIdContext, MessageType messageType, BinaryReader reader)
        {
            if (_isDisposed) return;

            if (messageType == MessageType._ClientConnectRequest)
            {
                Logger.Log($"[CoreComposer] Received _ClientConnectRequest from network source ID {sourceNetworkId}. EntityContext (should be 0): {entityIdContext}");

                ClientIdentity? identity = _connectionValidator.ValidateTokenAndGetIdentity(reader);

                if (identity.HasValue)
                {
                    ClientIdentity validatedIdentity = identity.Value;
                    if (_clientConnections.ContainsKey(validatedIdentity.ClientId))
                    {
                        Logger.LogWarning($"[CoreComposer] Client ID {validatedIdentity.ClientId} (from token) is already connected. Current policy: Rejecting new connection for this ID from source {sourceNetworkId}.");
                        // Optionally send a "connection failed: ID in use" message back to sourceNetworkId
                        // For now, just log and ignore. A more robust system might disconnect the old session.
                        return;
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
        }

        private ClientConnection CreateClientSessionAndEscadre(ClientIdentity identity, float initialPvsRadius, Vector3 initialSpawnPosition, int sourceNetworkId)
        {
            if (_isDisposed) { Logger.LogWarning("[CoreComposer] Attempted to create session on disposed composer."); return null; }
            if (_clientConnections.ContainsKey(identity.ClientId)) { 
                Logger.LogError($"[CoreComposer] CRITICAL: Attempt to create session for already existing ClientID {identity.ClientId} in CreateClientSessionAndEscadre. This should have been caught by HandleNetworkMessage_SessionManagement."); 
                return _clientConnections[identity.ClientId]; 
            }

            var clientConnection = new ClientConnection(identity.ClientId, initialPvsRadius); 
            _clientConnections.Add(identity.ClientId, clientConnection); 
            VisibilityManager.AddOrUpdateClientView(clientConnection);

            Escadre escadre = new Escadre(identity.ClientId, ServerLevel);
            if (!ServerLevel.AddEscadre(escadre))
            {
                Logger.LogError($"[CoreComposer] Failed to add escadre for client {identity.ClientId} to Level. Aborting client session creation.");
                VisibilityManager.RemoveClientView(identity.ClientId); 
                _clientConnections.Remove(identity.ClientId); 
                return null;
            }
            clientConnection.AssignEscadre(escadre);
            
            Ship initialShip = new DefaultShip(ServerLevel, escadre, initialSpawnPosition);
            escadre.AddShip(initialShip); 
            if (escadre.CurrentDestination.HasValue)
            {
                initialShip.SetMovementTarget(escadre.CurrentDestination.Value, _serverClock.CurrentTime);
            }

            Logger.Log($"[CoreComposer] Client Session Created & Registered: ID={identity.ClientId}, Nick='{identity.Nickname}', Admin={identity.IsAdmin}, AuthType={identity.AuthType}. Escadre and Ship created. PVS Radius: {initialPvsRadius}. NetworkSourceID: {sourceNetworkId}");

            ClientRegisteredEvent?.Invoke(clientConnection); 
            
            if (_networkLayer is MockNetworkLayer mockLayer)
            {
                mockLayer.MapNetworkSourceToClientId(sourceNetworkId, identity.ClientId);
                // No need to call mockLayer.RegisterMockClient here, MapNetworkSourceToClientId implies the game client ID is active
            }

            return clientConnection;
        }

        public void UnregisterClient(int clientId) 
        {
            if (_isDisposed) return;
            if (_clientConnections.TryGetValue(clientId, out ClientConnection clientConnection))
            {
                Logger.Log($"[CoreComposer] Unregistering Client {clientId} (Nickname: {clientConnection.EscadreInstance?.OwnerClientId /* this is not nickname */})."); // Nickname isn't easily available on ClientConnection
                ClientConnection tempConnectionReference = clientConnection; 

                if (_networkLayer is MockNetworkLayer mockLayer)
                {
                    // Find the networkSourceId associated with this gameClientId to unmap it
                    // This requires a reverse lookup or iterating the map in MockNetworkLayer
                    mockLayer.RemoveNetworkSourceMappingForClientId(clientId); 
                }

                VisibilityManager.RemoveClientView(clientId);
                if (clientConnection.EscadreInstance != null)
                {
                    clientConnection.EscadreInstance.Disband(true); 
                    ServerLevel.RemoveEscadre(clientConnection.EscadreInstance.OwnerClientId);
                }
                clientConnection.ClearEscadreReference(); 
                clientConnection.SetState(ClientState.Destroyed); 
                _clientConnections.Remove(clientId);

                ClientUnregisteredEvent?.Invoke(tempConnectionReference); 
            } else { Logger.LogWarning($"[CoreComposer] Attempted to unregister unknown client: {clientId}"); }
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