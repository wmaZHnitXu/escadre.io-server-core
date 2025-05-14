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
        public VisibilityManager VisibilityManager { get; } // Made public readonly
        private readonly ServerReplicationManager _replicationManager;
        private readonly IVisibilityStrategy _visibilityStrategy; // Store for disposal
        private bool _isDisposed = false;
        private readonly Dictionary<int, ClientConnection> _clientConnections = new Dictionary<int, ClientConnection>();
        public IReadOnlyDictionary<int, ClientConnection> ClientConnections => _clientConnections;

        // Constructor now accepts a visibility STRATEGY
        public CoreComposer(IServerNetworkLayer networkLayer, IVisibilityStrategy visibilityStrategy)
        {
            if (networkLayer == null) throw new ArgumentNullException(nameof(networkLayer));
            _visibilityStrategy = visibilityStrategy ?? throw new ArgumentNullException(nameof(visibilityStrategy));

            Logger.Log("[CoreComposer] Initializing Core Systems...");

            // 1. Initialize Model
            ServerLevel = new Level();
            Logger.Log("[CoreComposer] Level created.");

            // 2. Create VisibilityManager using the provided strategy
            VisibilityManager = new VisibilityManager(_visibilityStrategy); // Create and store
            Logger.Log("[CoreComposer] VisibilityManager created.");

            // 3. Initialize Networking, passing the created VisibilityManager and client connections dictionary
            _replicationManager = new ServerReplicationManager(ServerLevel, networkLayer, VisibilityManager, _clientConnections);
            Logger.Log("[CoreComposer] ServerReplicationManager created.");

            Logger.Log("[CoreComposer] Initialization Complete.");
        }

        // Client Management methods (RegisterClient, UnregisterClient) remain the same as previous correct version
        public ClientConnection RegisterClient(int clientId, float initialPvsRadius = 100f, Vector3? initialSpawnPosition = null)
        {
            if (_isDisposed) { Logger.LogWarning("[CoreComposer] Attempted to register client on disposed composer."); return null; }
            if (_clientConnections.ContainsKey(clientId)) { Logger.LogWarning($"[CoreComposer] Client {clientId} already registered."); return _clientConnections[clientId]; }

            var clientConnection = new ClientConnection(clientId, initialPvsRadius);
            _clientConnections.Add(clientId, clientConnection);
            VisibilityManager.AddOrUpdateClientView(clientConnection);

            Escadre escadre = new Escadre(clientId, ServerLevel);
            ServerLevel.AddEscadre(escadre);
            clientConnection.AssignEscadre(escadre);

            Vector3 spawnPos = initialSpawnPosition ?? clientConnection.Position;
            Ship initialShip = new Ship(ServerLevel, clientId, shipDesignId: 0, initialPosition: spawnPos);
            Logger.Log($"[CoreComposer] Client {clientId} registered. Escadre/Ship created. PVS Radius: {initialPvsRadius}");
            return clientConnection;
        }
        public void UnregisterClient(int clientId)
        {
            if (_isDisposed) return;
            if (_clientConnections.TryGetValue(clientId, out ClientConnection clientConnection))
            {
                Logger.Log($"[CoreComposer] Unregistering Client {clientId}.");
                VisibilityManager.RemoveClientView(clientId);
                if (clientConnection.EscadreInstance != null)
                {
                    clientConnection.EscadreInstance.Disband(true);
                    ServerLevel.RemoveEscadre(clientConnection.EscadreInstance.OwnerClientId);
                    clientConnection.ClearEscadreReference();
                }
                _clientConnections.Remove(clientId);
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
                    if (!entity.IsDead) VisibilityManager.UpdateEntityPosition(entity);
                }
                foreach (var clientConn in _clientConnections.Values)
                {
                    // Update PVS center for clients with active escadres
                    if (clientConn.CurrentState == ClientState.InSea && clientConn.EscadreInstance != null)
                    {
                        VisibilityManager.AddOrUpdateClientView(clientConn);
                    }
                    // For spectating clients, their camera position would need to be updated
                    // via a C->S message, which would then call AddOrUpdateClientView.
                }
                VisibilityManager.UpdateAllClientVisibility();
            }
            catch (Exception ex) { Logger.LogError($"[CoreComposer] Error during Update loop: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        public void Dispose()
        {
            if (_isDisposed) return; _isDisposed = true;
            Logger.Log("[CoreComposer] Disposing...");
            var clientIds = new List<int>(_clientConnections.Keys); // Operate on a copy
            foreach (var clientId in clientIds) { UnregisterClient(clientId); }
            _clientConnections.Clear();

            _replicationManager?.Dispose();
            VisibilityManager?.Dispose(); // CoreComposer created it, so it disposes it
            ServerLevel?.Destroy();
            _visibilityStrategy?.Dispose(); // CoreComposer also disposes the strategy it was given
            Logger.Log("[CoreComposer] Dispose complete.");
        }
    }
}