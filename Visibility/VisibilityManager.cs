// File: Scripts/Server/Core/Visibility/VisibilityManager.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Model;
using Core.Logging;

namespace Core.Visibility
{
    public class VisibilityManager : IDisposable
    {
        private readonly IVisibilityStrategy _strategy;
        private readonly Dictionary<int, Entity> _registeredEntities = new();
        private readonly Dictionary<int, IClientView> _registeredClients = new();
        private readonly Dictionary<int, HashSet<int>> _clientVisibleSets = new();
        private bool _isDisposed = false;

        public event Action<int, int> EntityEnteredPvs;
        public event Action<int, int> EntityLeftPvs;

        public VisibilityManager(IVisibilityStrategy strategy)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            Logger.Log("[VisibilityManager] Initialized.");
        }

        public void AddOrUpdateClientView(IClientView clientView)
        {
            if (_isDisposed || clientView == null) return;

            bool isNewClient = !_registeredClients.ContainsKey(clientView.ClientId);
            _registeredClients[clientView.ClientId] = clientView;
            _strategy.AddOrUpdateClientView(clientView);

            if (isNewClient)
            {
                _clientVisibleSets[clientView.ClientId] = new HashSet<int>();
                // Logger.Log($"[VisibilityManager] Added new Client View: {clientView.ClientId}");
                // Optionally, immediately update visibility for this new client
                // UpdateClientVisibility(clientView.ClientId);
            }
            // else { Logger.Log($"[VisibilityManager] Updated Client View: {clientView.ClientId}"); }
        }

        public void RemoveClientView(int clientId)
        {
            if (_isDisposed) return;
            if (_registeredClients.TryGetValue(clientId, out var clientView))
            {
                _strategy.RemoveClientView(clientView);
                _registeredClients.Remove(clientId);
                _clientVisibleSets.Remove(clientId);
                Logger.Log($"[VisibilityManager] Removed Client View: {clientId}");
            }
        }

        public void RegisterEntity(Entity entity)
        {
            if (_isDisposed || entity == null) return;
            // We register even if dead, so UnregisterEntity can clean it up properly from strategy
            // if it was previously alive and registered.
            // However, visibility calculation should only consider alive entities.

            if (!_registeredEntities.ContainsKey(entity.Id))
            {
                _registeredEntities.Add(entity.Id, entity);
                _strategy.AddOrUpdateEntity(entity); // Add to strategy
                // Logger.Log($"[VisibilityManager] Registered Entity: {entity.Id}");
            }
            else
            {
                _strategy.AddOrUpdateEntity(entity); // Update position in strategy
                // Logger.Log($"[VisibilityManager] Updated Entity Position: {entity.Id}");
            }
        }

        public void UnregisterEntity(int entityId)
        {
            if (_isDisposed) return;
            if (_registeredEntities.TryGetValue(entityId, out var entity))
            {
                _strategy.RemoveEntity(entity);
                _registeredEntities.Remove(entityId);
                // Logger.Log($"[VisibilityManager] Unregistered Entity: {entityId}");

                foreach (var kvp in _clientVisibleSets)
                {
                    if (kvp.Value.Remove(entityId))
                    {
                        EntityLeftPvs?.Invoke(kvp.Key, entityId);
                    }
                }
            }
        }

        public void UpdateAllClientVisibility()
        {
             if (_isDisposed) return;
            var clientIds = _registeredClients.Keys.ToList(); // Iterate a copy
             foreach (int clientId in clientIds)
             {
                 if(_registeredClients.ContainsKey(clientId)) // Check if client still exists
                    UpdateClientVisibility(clientId);
             }
        }

        public void UpdateClientVisibility(int clientId)
        {
            if (_isDisposed) return;
            if (!_registeredClients.TryGetValue(clientId, out var clientView)) return;
            if (!_clientVisibleSets.TryGetValue(clientId, out var previousVisibleSet))
            {
                 previousVisibleSet = new HashSet<int>();
                 _clientVisibleSets[clientId] = previousVisibleSet;
            }

            // Query strategy only for entities that are NOT dead.
            // The strategy itself might not know about IsDead.
            var currentVisibleCandidates = _strategy.FindVisibleEntities(clientView);
            var currentVisibleSet = new HashSet<int>();
            foreach(var entityId in currentVisibleCandidates)
            {
                if(_registeredEntities.TryGetValue(entityId, out var entity) && !entity.IsDead)
                {
                    currentVisibleSet.Add(entityId);
                }
            }


            var enteredEntities = currentVisibleSet.Except(previousVisibleSet).ToList();
            var leftEntities = previousVisibleSet.Except(currentVisibleSet).ToList();

            foreach (int entityId in enteredEntities)
            {
                EntityEnteredPvs?.Invoke(clientId, entityId);
                previousVisibleSet.Add(entityId);
            }
            foreach (int entityId in leftEntities)
            {
                EntityLeftPvs?.Invoke(clientId, entityId);
                previousVisibleSet.Remove(entityId);
            }
        }

        public bool IsEntityVisibleToClient(int clientId, int entityId)
        {
            if (_isDisposed) return false;
            return _clientVisibleSets.TryGetValue(clientId, out var visibleSet) && visibleSet.Contains(entityId);
        }

        public IEnumerable<int> GetClientsSeeingEntity(int entityId)
        {
             if (_isDisposed) return Enumerable.Empty<int>();
             return _clientVisibleSets
                 .Where(kvp => kvp.Value.Contains(entityId))
                 .Select(kvp => kvp.Key);
        }

        /// <summary>
        /// Gets the current PVS (Potentially Visible Set) of entity IDs for a specific client.
        /// This reflects the last calculated visibility state.
        /// </summary>
        public IReadOnlyCollection<int> GetPVSForClient(int clientId)
        {
            if (_isDisposed) return Array.Empty<int>();
            if (_clientVisibleSets.TryGetValue(clientId, out var visibleSet))
            {
                // Return a read-only copy or wrapper to prevent external modification
                return visibleSet.ToList().AsReadOnly(); // ToList creates a copy
            }
            return Array.Empty<int>(); // Client not found or no PVS calculated yet
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
            EntityEnteredPvs = null;
            EntityLeftPvs = null;
            Logger.Log("[VisibilityManager] Dispose complete.");
        }
    }
}