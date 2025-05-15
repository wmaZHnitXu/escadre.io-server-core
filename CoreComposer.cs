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

        public CoreComposer(IServerNetworkLayer networkLayer, IVisibilityStrategy visibilityStrategy)
        {
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
            _clientConnections.Add(clientId, clientConnection);
            VisibilityManager.AddOrUpdateClientView(clientConnection); // Register client view first

            Escadre escadre = new Escadre(clientId, ServerLevel);
            if (!ServerLevel.AddEscadre(escadre)) // Ensure escadre is added to level
            {
                Logger.LogError($"[CoreComposer] Failed to add escadre for client {clientId} to Level. Aborting client registration.");
                _clientConnections.Remove(clientId);
                VisibilityManager.RemoveClientView(clientId);
                return null;
            }
            clientConnection.AssignEscadre(escadre); // Assign escadre to client connection

            Vector3 spawnPos = initialSpawnPosition ?? clientConnection.Position; // clientConnection.Position might be 0,0,0 if no escadre yet
                                                                                  // Better to ensure spawnPos is reasonable if not provided.
            if (!initialSpawnPosition.HasValue) spawnPos = new Vector3(UnityEngine.Random.Range(-50f,50f), 0, UnityEngine.Random.Range(-50f,50f));


            // Instantiate a concrete ship type, e.g., DefaultShip
            // The Ship's constructor now takes the Escadre instance.
            // The Ship's constructor will call _level.AddEntity(this).
            Ship initialShip = new DefaultShip(ServerLevel, escadre, spawnPos);

            // Explicitly add the created ship to its escadre's management list
            escadre.AddShip(initialShip);

            Logger.Log($"[CoreComposer] Client {clientId} registered. Escadre {escadre.OwnerClientId} and initial Ship (ID pending: {initialShip.Id}) created. PVS Radius: {initialPvsRadius}");
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
                    clientConnection.EscadreInstance.Disband(true); // Kills ships
                    ServerLevel.RemoveEscadre(clientConnection.EscadreInstance.OwnerClientId); // Removes escadre from level
                }
                // ClearEscadreReference is implicitly handled by AssignEscadre(null) or state changes,
                // but can be called explicitly if needed. Here, escadre is gone.
                clientConnection.ClearEscadreReference(); // Ensure ClientConnection no longer holds it

                _clientConnections.Remove(clientId);
            } else { Logger.LogWarning($"[CoreComposer] Attempted to unregister unknown client: {clientId}"); }
        }


        public void Update(float deltaTime)
        {
            if (_isDisposed) return;
            try
            {
                // 1. Update Game Model (Entities, Level systems)
                ServerLevel.DoUpdate(deltaTime); // This updates all entities, including ships

                // 2. Update Visibility System
                // Update entity positions within the visibility strategy
                foreach (var entity in ServerLevel.GetAllEntities()) // Consider only entities that moved or changed state
                {
                    if (!entity.IsDead) // Only track alive entities for visibility updates
                    {
                        VisibilityManager.RegisterEntity(entity); // Use RegisterEntity, it handles AddOrUpdate
                    }
                    // If an entity just died, ServerReplicationManager handles unregistering it from visibility via HandleEntityDeath.
                }

                // Update client view positions/radii in the visibility strategy
                foreach (var clientConn in _clientConnections.Values)
                {
                    // IClientView.Position for PVS is driven by Escadre's center or spectator cam.
                    // Escadre.CalculateCenterPoint() is called when clientConn.Position is accessed.
                    // VisibilityManager.AddOrUpdateClientView will refresh it.
                    VisibilityManager.AddOrUpdateClientView(clientConn);
                }

                // Recalculate PVS for all clients
                VisibilityManager.UpdateAllClientVisibility();

                // 3. Replication Manager handles messages based on visibility changes (done via events)
                // _replicationManager doesn't have an Update method; it's event-driven.
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
            ServerLevel?.Destroy(); // Destroys all entities in the level
            _visibilityStrategy?.Dispose();
            Logger.Log("[CoreComposer] Dispose complete.");
        }
    }
}