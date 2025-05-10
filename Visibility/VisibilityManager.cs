// File: Scripts/Server/Core/Visibility/VisibilityManager.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Model; // Entity
using Core.Logging;

namespace Core.Visibility
{
    /// <summary>
    /// Manages visibility sets for clients based on entity and client positions,
    /// using a provided IVisibilityStrategy. Raises events when an entity enters
    /// or leaves a client's Possibly Visible Set (PVS).
    /// </summary>
    public class VisibilityManager : IDisposable
    {
        private readonly IVisibilityStrategy _strategy;
        private readonly Dictionary<int, Entity> _registeredEntities = new();
        private readonly Dictionary<int, IClientView> _registeredClients = new();

        // Tracks the last known visible set for each client to detect changes
        private readonly Dictionary<int, HashSet<int>> _clientVisibleSets = new();
        private bool _isDisposed = false;

        // Events indicating visibility changes
        public event Action<int /*clientId*/, int /*entityId*/> EntityEnteredPvs;
        public event Action<int /*clientId*/, int /*entityId*/> EntityLeftPvs;

        public VisibilityManager(IVisibilityStrategy strategy)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            Logger.Log("[VisibilityManager] Initialized.");
        }

        // --- Client Management ---

        public void AddOrUpdateClientView(IClientView clientView)
        {
            if (_isDisposed) return;
            if (clientView == null) return;

            _registeredClients[clientView.ClientId] = clientView;
            _strategy.AddOrUpdateClientView(clientView);
            // Initialize visible set if new client
            if (!_clientVisibleSets.ContainsKey(clientView.ClientId))
            {
                _clientVisibleSets[clientView.ClientId] = new HashSet<int>();
            }
            // Consider triggering an immediate visibility check for this client?
            // UpdateClientVisibility(clientView.ClientId); // Could be expensive if called often
        }

        public void RemoveClientView(int clientId)
        {
            if (_isDisposed) return;
            if (_registeredClients.TryGetValue(clientId, out var clientView))
            {
                _strategy.RemoveClientView(clientView);
                _registeredClients.Remove(clientId);
                _clientVisibleSets.Remove(clientId); // Clean up tracking data
                Logger.Log($"[VisibilityManager] Removed Client View: {clientId}");
            }
        }

        // --- Entity Management ---

        public void RegisterEntity(Entity entity)
        {
            if (_isDisposed || entity == null || entity.IsDead) return; // Don't register dead entities
            if (!_registeredEntities.ContainsKey(entity.Id))
            {
                _registeredEntities.Add(entity.Id, entity);
                _strategy.AddOrUpdateEntity(entity);
                // Hook position changes? Requires modification to Entity/Level or polling
                // entity.OnPositionChanged += HandleEntityPositionChange; // Ideal but needs event
                // Logger.Log($"[VisibilityManager] Registered Entity: {entity.Id}");
            }
            else
            {
                 // Entity already registered, likely just an update needed
                 UpdateEntityPosition(entity);
            }
        }

        public void UnregisterEntity(int entityId)
        {
            if (_isDisposed) return;
            if (_registeredEntities.TryGetValue(entityId, out var entity))
            {
                // entity.OnPositionChanged -= HandleEntityPositionChange; // Unsubscribe if using events
                _strategy.RemoveEntity(entity);
                _registeredEntities.Remove(entityId);
                // Logger.Log($"[VisibilityManager] Unregistered Entity: {entityId}");

                // Notify any clients still seeing this entity that it's gone from visibility perspective
                // Note: Actual destruction message (if entity died) is handled elsewhere
                 foreach (var kvp in _clientVisibleSets)
                 {
                     if (kvp.Value.Remove(entityId)) // If the client was seeing it
                     {
                         EntityLeftPvs?.Invoke(kvp.Key, entityId); // Signal it left PVS due to unregistration
                     }
                 }
            }
        }

        // Call this frequently if Entity doesn't have position changed events
        public void UpdateEntityPosition(Entity entity)
        {
             if (_isDisposed || entity == null || !_registeredEntities.ContainsKey(entity.Id)) return;
            _strategy.AddOrUpdateEntity(entity);
            // Optional: Trigger visibility update for clients near the entity? Expensive.
            // UpdateVisibilityForNearbyClients(entity.Position, someRadius);
        }

        // --- Visibility Calculation ---

        /// <summary>
        /// Periodically recalculates visibility for all registered clients.
        /// This is the main driver for PVS updates. Call this from the server loop.
        /// </summary>
        public void UpdateAllClientVisibility()
        {
             if (_isDisposed) return;
             // Create copy of keys to prevent modification issues if clients disconnect during update
            var clientIds = _registeredClients.Keys.ToList();
             foreach (int clientId in clientIds)
             {
                 if(_registeredClients.ContainsKey(clientId)) // Check if still connected
                    UpdateClientVisibility(clientId);
             }
        }

        /// <summary>
        /// Recalculates the PVS for a single client and raises events for changes.
        /// </summary>
        /// <param name="clientId">The ID of the client to update.</param>
        public void UpdateClientVisibility(int clientId)
        {
            if (_isDisposed) return;
            if (!_registeredClients.TryGetValue(clientId, out var clientView))
            {
                // Logger.LogWarning($"[VisibilityManager] Attempted to update visibility for unknown client: {clientId}");
                return;
            }
            if (!_clientVisibleSets.TryGetValue(clientId, out var previousVisibleSet))
            {
                 Logger.LogWarning($"[VisibilityManager] Client visible set not initialized for client: {clientId}");
                 previousVisibleSet = new HashSet<int>(); // Initialize if somehow missing
                 _clientVisibleSets[clientId] = previousVisibleSet;
            }

            // 1. Query the strategy for the current set of potentially visible entities
            var currentVisibleSet = new HashSet<int>(_strategy.FindVisibleEntities(clientView));

            // 2. Compare with the previous set to find differences
            // Entities that entered the PVS
            var enteredEntities = currentVisibleSet.Except(previousVisibleSet).ToList(); // ToList to execute query

            // Entities that left the PVS
            var leftEntities = previousVisibleSet.Except(currentVisibleSet).ToList(); // ToList to execute query

            // 3. Raise events for changes
            foreach (int entityId in enteredEntities)
            {
                if (_registeredEntities.ContainsKey(entityId)) // Ensure entity still exists
                {
                    // Logger.Log($"[VisibilityManager] Entity {entityId} entered PVS for Client {clientId}"); // DEBUG
                    EntityEnteredPvs?.Invoke(clientId, entityId);
                    previousVisibleSet.Add(entityId); // Update tracked set
                }
            }

            foreach (int entityId in leftEntities)
            {
                 // Logger.Log($"[VisibilityManager] Entity {entityId} left PVS for Client {clientId}"); // DEBUG
                 EntityLeftPvs?.Invoke(clientId, entityId);
                 previousVisibleSet.Remove(entityId); // Update tracked set
            }

            // No need to assign _clientVisibleSets[clientId] = previousVisibleSet; we modified it in place.
        }


        /// <summary>
        /// Checks if a specific entity is currently considered visible by a specific client.
        /// Note: This checks the last calculated state.
        /// </summary>
        public bool IsEntityVisibleToClient(int clientId, int entityId)
        {
            if (_isDisposed) return false;
            return _clientVisibleSets.TryGetValue(clientId, out var visibleSet) && visibleSet.Contains(entityId);
        }

        /// <summary>
        /// Gets the set of client IDs currently seeing a specific entity.
        /// Note: This checks the last calculated state.
        /// </summary>
        public IEnumerable<int> GetClientsSeeingEntity(int entityId)
        {
             if (_isDisposed) return Enumerable.Empty<int>();
             // This is less efficient than storing the reverse map, but simpler for now
             return _clientVisibleSets
                 .Where(kvp => kvp.Value.Contains(entityId))
                 .Select(kvp => kvp.Key);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Logger.Log("[VisibilityManager] Disposing...");
            _strategy?.Dispose();
            _registeredEntities.Clear();
            _registeredClients.Clear();
            _clientVisibleSets.Clear();
            EntityEnteredPvs = null; // Clear events
            EntityLeftPvs = null;
            Logger.Log("[VisibilityManager] Dispose complete.");
        }
    }
}