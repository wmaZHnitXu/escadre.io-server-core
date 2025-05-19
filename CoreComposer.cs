// File: Core/CoreComposer.cs
using System;
using System.Collections.Generic;
using Core.Model;
using Core.Network;
using Core.Logging;
using Core.Visibility;
using Core.Session;
using Core.Primitives;
using Core.Time; // For IClock

namespace Core
{
    public class CoreComposer : IDisposable
    {
        public Level ServerLevel { get; }
        public VisibilityManager VisibilityManager { get; }
        private readonly ServerReplicationManager _replicationManager;
        private readonly IVisibilityStrategy _visibilityStrategy;
        private readonly IClock _serverClock; // Added server-side clock
        private bool _isDisposed = false;
        private readonly Dictionary<int, ClientConnection> _clientConnections = new Dictionary<int, ClientConnection>();
        public IReadOnlyDictionary<int, ClientConnection> ClientConnections => _clientConnections;

        public event Action<ClientConnection> ClientRegisteredEvent;
        public event Action<ClientConnection> ClientUnregisteredEvent;

        // Constructor updated to accept IClock
        public CoreComposer(IServerNetworkLayer networkLayer, IVisibilityStrategy visibilityStrategy, IClock serverClock)
        {
            if (networkLayer == null) throw new ArgumentNullException(nameof(networkLayer));
            _visibilityStrategy = visibilityStrategy ?? throw new ArgumentNullException(nameof(visibilityStrategy));
            _serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock)); // Inject server clock

            Logger.Log("[CoreComposer] Initializing Core Systems...");

            ServerLevel = new Level();
            Logger.Log("[CoreComposer] Level created.");

            VisibilityManager = new VisibilityManager(_visibilityStrategy);
            Logger.Log("[CoreComposer] VisibilityManager created.");

            // ServerReplicationManager now needs to pass serverTime to ClientConnection methods when handling C->S commands
            // This implies ServerReplicationManager might also need access to the IClock, or CoreComposer handles commands.
            // For now, ServerReplicationManager will get it from ClientConnection's methods being called by CoreComposer or similar.
            // Let's assume for now that command handling that needs serverTime is done at a level that has the clock.
            // ServerReplicationManager's HandleClientMessage will call ClientConnection methods, which now require serverTime.
            // So, ServerReplicationManager needs the clock.
            _replicationManager = new ServerReplicationManager(ServerLevel, networkLayer, VisibilityManager, _clientConnections, _serverClock);
            Logger.Log("[CoreComposer] ServerReplicationManager created.");

            Logger.Log("[CoreComposer] Initialization Complete.");
        }

        public ClientConnection RegisterClient(int clientId, float initialPvsRadius = 100f, Vector3? initialSpawnPosition = null)
        {
            if (_isDisposed) { Logger.LogWarning("[CoreComposer] Attempted to register client on disposed composer."); return null; }
            if (_clientConnections.ContainsKey(clientId)) { Logger.LogWarning($"[CoreComposer] Client {clientId} already registered."); return _clientConnections[clientId]; }

            var clientConnection = new ClientConnection(clientId, initialPvsRadius);
            _clientConnections.Add(clientId, clientConnection); 
            VisibilityManager.AddOrUpdateClientView(clientConnection);

            Escadre escadre = new Escadre(clientId, ServerLevel);
            if (!ServerLevel.AddEscadre(escadre))
            {
                Logger.LogError($"[CoreComposer] Failed to add escadre for client {clientId} to Level. Aborting client registration.");
                VisibilityManager.RemoveClientView(clientId); 
                _clientConnections.Remove(clientId); 
                return null;
            }
            clientConnection.AssignEscadre(escadre);

            // UnityEngine.Random.Range was removed. Spawning logic needs to be handled by caller or a dedicated service.
            // For now, let's use a deterministic offset or require initialSpawnPosition.
            Vector3 spawnPos = initialSpawnPosition ?? new Vector3((clientId % 5) * 10f - 20f, 0, (clientId / 5) * 10f - 20f); // Simple deterministic spawn
            
            Ship initialShip = new DefaultShip(ServerLevel, escadre, spawnPos);
            // DefaultShip constructor adds to level. AddShip adds to escadre's list.
            // If the new ship needs an immediate move command based on escadre's current orders:
            if (escadre.CurrentDestination.HasValue)
            {
                initialShip.SetMovementTarget(escadre.CurrentDestination.Value, _serverClock.CurrentTime);
            }
            // Note: Escadre.AddShip was internal and didn't take serverTime.
            // If new ships need to sync to escadre orders immediately upon creation by Escadre itself (e.g. RequestBuyShip),
            // then Escadre methods would also need serverTime.
            // For now, this explicit SetMovementTarget after creation by CoreComposer is fine.
            escadre.AddShip(initialShip); // This was already there, ensuring it's after potential SetMovementTarget

            Logger.Log($"[CoreComposer] Client {clientId} registered. Escadre {escadre.OwnerClientId} and initial Ship (ID pending: {initialShip.Id}) created at {spawnPos}. PVS Radius: {initialPvsRadius}");

            ClientRegisteredEvent?.Invoke(clientConnection); 
            return clientConnection;
        }

        public void UnregisterClient(int clientId)
        {
            if (_isDisposed) return;
            if (_clientConnections.TryGetValue(clientId, out ClientConnection clientConnection))
            {
                Logger.Log($"[CoreComposer] Unregistering Client {clientId}.");
                ClientConnection tempConnectionReference = clientConnection; 

                VisibilityManager.RemoveClientView(clientId);
                if (clientConnection.EscadreInstance != null)
                {
                    clientConnection.EscadreInstance.Disband(true); // silentKill = true
                    ServerLevel.RemoveEscadre(clientConnection.EscadreInstance.OwnerClientId);
                }
                clientConnection.ClearEscadreReference();
                _clientConnections.Remove(clientId);

                ClientUnregisteredEvent?.Invoke(tempConnectionReference); 
            } else { Logger.LogWarning($"[CoreComposer] Attempted to unregister unknown client: {clientId}"); }
        }

        public void Update(float deltaTime)
        {
            if (_isDisposed) return;
            try
            {
                // ServerClock's CurrentTime will advance implicitly or explicitly based on its implementation.
                // The deltaTime is from the main loop.
                ServerLevel.DoUpdate(deltaTime);
                
                // Update entity positions in visibility manager
                foreach (var entity in ServerLevel.GetAllEntities())
                {
                    // Only register/update alive entities for visibility calculations,
                    // but VisibilityManager.RegisterEntity can handle dead ones for removal if previously seen.
                    // The strategy itself should filter by IsDead if it caches entities.
                    VisibilityManager.RegisterEntity(entity); 
                }

                // Update client view positions in visibility manager
                foreach (var clientConn in _clientConnections.Values)
                {
                    VisibilityManager.AddOrUpdateClientView(clientConn);
                }
                VisibilityManager.UpdateAllClientVisibility();
            }
            catch (Exception ex) { Logger.LogError($"[CoreComposer] Error during Update loop: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        public void Dispose()
        {
            if (_isDisposed) return; _isDisposed = true;
            Logger.Log("[CoreComposer] Disposing...");
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