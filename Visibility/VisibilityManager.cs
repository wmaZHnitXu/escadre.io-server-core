// File: Scripts/Server/Core/Visibility/VisibilityManager.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Model;
using Core.Logging;
using Core.Time; 
using Core.Primitives; 

namespace Core.Visibility
{
    public class VisibilityManager : IDisposable
    {
        private readonly IVisibilityStrategy _strategy;
        private readonly IClock _clock; 
        private readonly Dictionary<int, Entity> _registeredEntities = new();
        private readonly Dictionary<int, IClientView> _registeredClients = new();
        private readonly Dictionary<int, HashSet<int>> _clientVisibleSets = new();
        
        private const float StationaryClientPvsUpdateInterval = 0.5f; 
        private const float ClientMovementPvsUpdateThresholdSq = 1.0f * 1.0f; 
        private readonly Dictionary<int, float> _clientLastPvsUpdateTime = new();
        private readonly Dictionary<int, Vector3> _clientLastPvsUpdatePosition = new();

        private bool _isDisposed = false;

        public event Action<int, int> EntityEnteredPvs;
        public event Action<int, int> EntityLeftPvs;

        public VisibilityManager(IVisibilityStrategy strategy, IClock clock) 
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock)); // Clock is now mandatory for throttling
            Logger.Log("[VisibilityManager] Initialized.");
        }

        public void AddOrUpdateClientView(IClientView clientView)
        {
            if (_isDisposed || clientView == null) return;

            bool isNewClient = !_registeredClients.ContainsKey(clientView.ClientId);
            Vector3 oldPosition = Vector3.Zero;
            if (!isNewClient && _clientLastPvsUpdatePosition.TryGetValue(clientView.ClientId, out var pos))
            {
                oldPosition = pos;
            }

            _registeredClients[clientView.ClientId] = clientView;
            _strategy.AddOrUpdateClientView(clientView);

            if (isNewClient)
            {
                _clientVisibleSets[clientView.ClientId] = new HashSet<int>();
                _clientLastPvsUpdateTime[clientView.ClientId] = -StationaryClientPvsUpdateInterval; // Force initial update
                _clientLastPvsUpdatePosition[clientView.ClientId] = clientView.Position;
            }
            else
            {
                // If client moved, ensure its _clientLastPvsUpdatePosition is updated
                // so the next PVS check uses the correct "previous" position for movement threshold.
                // The actual PVS update will be throttled by UpdateAllClientVisibility.
                // No, this update happens *within* UpdateAllClientVisibility after PVS calc.
                // Here, we just acknowledge the clientView might have new data.
            }
        }

        public void RemoveClientView(int clientId)
        {
            if (_isDisposed) return;
            if (_registeredClients.TryGetValue(clientId, out var clientView))
            {
                _strategy.RemoveClientView(clientView);
                _registeredClients.Remove(clientId);
                _clientVisibleSets.Remove(clientId);
                _clientLastPvsUpdateTime.Remove(clientId);
                _clientLastPvsUpdatePosition.Remove(clientId);
                Logger.Log($"[VisibilityManager] Removed Client View: {clientId}");
            }
        }

        public void RegisterEntity(Entity entity)
        {
            if (_isDisposed || entity == null) return;
            
            // The IVisibilityStrategy (spatial index) is primarily updated by Level.cs
            // when entities are added, removed, or move.
            // This method ensures VisibilityManager's internal _registeredEntities list is up-to-date.
            if (!_registeredEntities.ContainsKey(entity.Id))
            {
                _registeredEntities.Add(entity.Id, entity);
            }
            else
            {
                _registeredEntities[entity.Id] = entity; // Update reference if it changed
            }

            if (entity.IsDead) 
            {
                RemoveEntityFromAllClientPVS(entity.Id);
                // Also ensure strategy knows it's dead / removed from active set
                // Level.cs handles strategy.RemoveEntity on actual removal.
                // If it's just marked dead but not yet removed from Level's list, strategy might still have it.
                // It's safer if strategy queries only consider non-dead entities, or Level removes from strategy promptly.
                // Current strategy.FindVisibleEntities is expected to return only alive entities (via filter in VM).
            }
        }

        public void UnregisterEntity(int entityId)
        {
            if (_isDisposed) return;
            _registeredEntities.Remove(entityId);
            RemoveEntityFromAllClientPVS(entityId);
            // Strategy removal is handled by Level.cs
        }
        
        private void RemoveEntityFromAllClientPVS(int entityId)
        {
            foreach (var kvp in _clientVisibleSets) // Iterate copy if modifying
            {
                HashSet<int> visibleSet = kvp.Value;
                if (visibleSet.Remove(entityId))
                {
                    EntityLeftPvs?.Invoke(kvp.Key, entityId);
                }
            }
        }

        public void UpdateAllClientVisibility()
        {
            if (_isDisposed) return;
            var clientIds = _registeredClients.Keys.ToList(); 
            float currentTime = _clock.CurrentTime;

            foreach (int clientId in clientIds)
            {
                if (!_registeredClients.TryGetValue(clientId, out var clientView)) continue;

                bool needsPvsUpdate = false;
                // Ensure throttling data exists (should be set in AddOrUpdateClientView)
                if (!_clientLastPvsUpdateTime.ContainsKey(clientId))
                {
                     _clientLastPvsUpdateTime[clientId] = -StationaryClientPvsUpdateInterval; 
                     _clientLastPvsUpdatePosition[clientId] = clientView.Position;
                }

                if ((clientView.Position - _clientLastPvsUpdatePosition[clientId]).SqrMagnitude > ClientMovementPvsUpdateThresholdSq)
                {
                    needsPvsUpdate = true;
                }
                else if (currentTime - _clientLastPvsUpdateTime[clientId] >= StationaryClientPvsUpdateInterval)
                {
                    needsPvsUpdate = true;
                }
                

                if (needsPvsUpdate)
                {
                    // Logger.Log($"[VisibilityManager] Updating PVS for Client {clientId}. Reason: {(clientView.Position - _clientLastPvsUpdatePosition[clientId]).SqrMagnitude > ClientMovementPvsUpdateThresholdSq} move, {currentTime - _clientLastPvsUpdateTime[clientId] >= StationaryClientPvsUpdateInterval} time");
                    UpdateClientVisibility(clientId);
                    _clientLastPvsUpdateTime[clientId] = currentTime;
                    _clientLastPvsUpdatePosition[clientId] = clientView.Position;
                }
            }
        }

        // Made public for potential direct calls if needed, but usually called by UpdateAllClientVisibility
        public void UpdateClientVisibility(int clientId) 
        {
            if (_isDisposed) return;
            if (!_registeredClients.TryGetValue(clientId, out var clientView)) return;
            if (!_clientVisibleSets.TryGetValue(clientId, out var previousVisibleSet))
            {
                 // This case should ideally not happen if AddOrUpdateClientView correctly initializes.
                 Logger.LogWarning($"[VisibilityManager] Client {clientId} had no previousVisibleSet. Creating one.");
                 previousVisibleSet = new HashSet<int>();
                 _clientVisibleSets[clientId] = previousVisibleSet;
            }

            var currentVisibleCandidates = _strategy.FindVisibleEntities(clientView);
            var currentVisibleSet = new HashSet<int>(); // Will store IDs of entities that are alive and in range

            foreach(var entityId in currentVisibleCandidates)
            {
                if(_registeredEntities.TryGetValue(entityId, out var entity) && !entity.IsDead)
                {
                    currentVisibleSet.Add(entityId);
                }
            }
            
            // Find entities that entered PVS
            foreach (int entityIdInCurrent in currentVisibleSet)
            {
                if (!previousVisibleSet.Contains(entityIdInCurrent)) // It's new
                {
                    previousVisibleSet.Add(entityIdInCurrent); // Add to the persistent set for this client
                    EntityEnteredPvs?.Invoke(clientId, entityIdInCurrent);
                }
            }

            // Find entities that left PVS
            List<int> leftPvsBuffer = null; // Lazy init
            foreach (int entityIdInPrevious in previousVisibleSet)
            {
                if (!currentVisibleSet.Contains(entityIdInPrevious)) // It's no longer in current
                {
                    if (leftPvsBuffer == null) leftPvsBuffer = new List<int>();
                    leftPvsBuffer.Add(entityIdInPrevious);
                    EntityLeftPvs?.Invoke(clientId, entityIdInPrevious);
                }
            }

            if (leftPvsBuffer != null)
            {
                foreach (int entityIdToRemove in leftPvsBuffer)
                {
                    previousVisibleSet.Remove(entityIdToRemove); // Remove from the persistent set
                }
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
             List<int> clients = new List<int>();
             foreach(var kvp in _clientVisibleSets)
             {
                 if (kvp.Value.Contains(entityId))
                 {
                     clients.Add(kvp.Key);
                 }
             }
             return clients;
        }
        
        public IReadOnlyCollection<int> GetPVSForClient(int clientId)
        {
            if (_isDisposed) return Array.Empty<int>();
            if (_clientVisibleSets.TryGetValue(clientId, out var visibleSet))
            {
                return visibleSet.ToList().AsReadOnly(); 
            }
            return Array.Empty<int>(); 
        }


        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Logger.Log("[VisibilityManager] Disposing...");
            
            // The strategy is passed in, so CoreComposer (its creator) is responsible for disposing it.
            // (_strategy as IDisposable)?.Dispose(); 
            
            _registeredEntities.Clear();
            _registeredClients.Clear();
            _clientVisibleSets.Clear();
            _clientLastPvsUpdateTime.Clear();
            _clientLastPvsUpdatePosition.Clear();
            EntityEnteredPvs = null;
            EntityLeftPvs = null;
            Logger.Log("[VisibilityManager] Dispose complete.");
        }
    }
}