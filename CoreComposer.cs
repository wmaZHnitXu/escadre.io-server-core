// File: Scripts/Server/Core/CoreComposer.cs
using System;
using System.Collections.Generic;
using Core.Model;
using Core.Network;
using Core.Logging;
using Core.Visibility;
using Core.Session;
using Core.Primitives;

namespace Core
{
    public class CoreComposer : IDisposable
    {
        public Level ServerLevel { get; }
        public VisibilityManager VisibilityManager { get; }
        private readonly ServerReplicationManager _replicationManager;
        private readonly IVisibilityStrategy _visibilityStrategy;
        private bool _isDisposed = false;
        private readonly Dictionary<int, ClientConnection> _clientConnections = new Dictionary<int, ClientConnection>();
        public IReadOnlyDictionary<int, ClientConnection> ClientConnections => _clientConnections;

        // --- Events for client lifecycle ---
        public event Action<ClientConnection> ClientRegisteredEvent;
        public event Action<ClientConnection> ClientUnregisteredEvent;
        // ---

        public CoreComposer(IServerNetworkLayer networkLayer, IVisibilityStrategy visibilityStrategy)
        {
            // ... (constructor remains the same) ...
            if (networkLayer == null) throw new ArgumentNullException(nameof(networkLayer));
            _visibilityStrategy = visibilityStrategy ?? throw new ArgumentNullException(nameof(visibilityStrategy));

            Logger.Log("[CoreComposer] Initializing Core Systems...");

            ServerLevel = new Level();
            Logger.Log("[CoreComposer] Level created.");

            VisibilityManager = new VisibilityManager(_visibilityStrategy);
            Logger.Log("[CoreComposer] VisibilityManager created.");

            _replicationManager = new ServerReplicationManager(ServerLevel, networkLayer, VisibilityManager, _clientConnections);
            Logger.Log("[CoreComposer] ServerReplicationManager created.");

            Logger.Log("[CoreComposer] Initialization Complete.");
        }

        public ClientConnection RegisterClient(int clientId, float initialPvsRadius = 100f, Vector3? initialSpawnPosition = null)
        {
            if (_isDisposed) { Logger.LogWarning("[CoreComposer] Attempted to register client on disposed composer."); return null; }
            if (_clientConnections.ContainsKey(clientId)) { Logger.LogWarning($"[CoreComposer] Client {clientId} already registered."); return _clientConnections[clientId]; }

            var clientConnection = new ClientConnection(clientId, initialPvsRadius);
            _clientConnections.Add(clientId, clientConnection); // Add first
            VisibilityManager.AddOrUpdateClientView(clientConnection);

            Escadre escadre = new Escadre(clientId, ServerLevel);
            if (!ServerLevel.AddEscadre(escadre))
            {
                Logger.LogError($"[CoreComposer] Failed to add escadre for client {clientId} to Level. Aborting client registration.");
                VisibilityManager.RemoveClientView(clientId); // Clean up PVS registration
                _clientConnections.Remove(clientId); // Clean up client connection registration
                return null;
            }
            clientConnection.AssignEscadre(escadre);

            Vector3 spawnPos = initialSpawnPosition ?? new Vector3(UnityEngine.Random.Range(-50f,50f), 0, UnityEngine.Random.Range(-50f,50f));
            Ship initialShip = new DefaultShip(ServerLevel, escadre, spawnPos);
            escadre.AddShip(initialShip);

            Logger.Log($"[CoreComposer] Client {clientId} registered. Escadre {escadre.OwnerClientId} and initial Ship (ID pending: {initialShip.Id}) created. PVS Radius: {initialPvsRadius}");

            ClientRegisteredEvent?.Invoke(clientConnection); // Invoke event
            return clientConnection;
        }

        public void UnregisterClient(int clientId)
        {
            if (_isDisposed) return;
            if (_clientConnections.TryGetValue(clientId, out ClientConnection clientConnection))
            {
                Logger.Log($"[CoreComposer] Unregistering Client {clientId}.");
                ClientConnection tempConnectionReference = clientConnection; // Hold reference for event

                VisibilityManager.RemoveClientView(clientId);
                if (clientConnection.EscadreInstance != null)
                {
                    clientConnection.EscadreInstance.Disband(true);
                    ServerLevel.RemoveEscadre(clientConnection.EscadreInstance.OwnerClientId);
                }
                clientConnection.ClearEscadreReference();
                _clientConnections.Remove(clientId);

                ClientUnregisteredEvent?.Invoke(tempConnectionReference); // Invoke event with the removed connection
            } else { Logger.LogWarning($"[CoreComposer] Attempted to unregister unknown client: {clientId}"); }
        }

        public void Update(float deltaTime)
        {
            // ... (Update method remains the same) ...
            if (_isDisposed) return;
            try
            {
                ServerLevel.DoUpdate(deltaTime);
                foreach (var entity in ServerLevel.GetAllEntities())
                {
                    if (!entity.IsDead)
                    {
                        VisibilityManager.RegisterEntity(entity);
                    }
                }
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
            foreach (var clientId in clientIds) { UnregisterClient(clientId); } // Unregister will trigger event
            _clientConnections.Clear();

            _replicationManager?.Dispose();
            VisibilityManager?.Dispose();
            ServerLevel?.Destroy();
            _visibilityStrategy?.Dispose();

            // Clear event subscribers
            ClientRegisteredEvent = null;
            ClientUnregisteredEvent = null;

            Logger.Log("[CoreComposer] Dispose complete.");
        }
    }
}