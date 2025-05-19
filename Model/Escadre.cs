// File: Core/Model/Escadre.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    public class Escadre
    {
        public int OwnerClientId { get; }
        private readonly Level _level;

        private readonly List<int> _shipEntityIds = new List<int>();
        public IReadOnlyList<int> ShipEntityIds => _shipEntityIds.AsReadOnly();

        private Vector2? _currentDestination;
        public Vector2? CurrentDestination { get => _currentDestination; private set => _currentDestination = value; }

        private readonly HashSet<int> _targetEscadreOwnerClientIds = new HashSet<int>();
        public IReadOnlyCollection<int> TargetEscadreOwnerClientIds => _targetEscadreOwnerClientIds;

        private int _resources;
        public int Resources { get => _resources; private set => _resources = value; }

        public Escadre(int ownerClientId, Level level)
        {
            OwnerClientId = ownerClientId;
            _level = level ?? throw new ArgumentNullException(nameof(level));
            _resources = 100; 
        }

        public void AddResources(int amount)
        {
            if (amount <= 0) {
                return;
            }
            _resources += amount;
        }

        private int GetUpgradeCostForShip(Ship ship)
        {
            return 50;
        }

        internal void AddShip(Ship ship)
        {
            if (ship == null || ship.OwningEscadreClientId != OwnerClientId) {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Attempted to add invalid ship (null or wrong owner). Ship ID: {ship?.Id}");
                return;
            }
            if (!_shipEntityIds.Contains(ship.Id)) {
                _shipEntityIds.Add(ship.Id);
                Logger.Log($"[Escadre {OwnerClientId}] Added Ship {ship.Id}. Total ships: {_shipEntityIds.Count}");
            }
        }

        private void RemoveShip(int shipId)
        {
            if (_shipEntityIds.Remove(shipId)) {
                Logger.Log($"[Escadre {OwnerClientId}] Removed Ship {shipId}. Total ships: {_shipEntityIds.Count}");
                if (!_shipEntityIds.Any()) {
                    Logger.Log($"[Escadre {OwnerClientId}] All ships lost!");
                }
            }
        }

        internal void HandleShipDestroyed(int shipId)
        {
            RemoveShip(shipId);
        }

        public void SetCourse(Vector2 destination, float serverTime)
        {
            CurrentDestination = destination;
            if (TargetEscadreOwnerClientIds.Any()) {
                _targetEscadreOwnerClientIds.Clear();
                Logger.Log($"[Escadre {OwnerClientId}] Setting course to {destination}. Cancelling attack orders.");
            } else {
                Logger.Log($"[Escadre {OwnerClientId}] Setting course to {destination}.");
            } 

            foreach (int shipId in _shipEntityIds) {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead) {
                    ship.SetMovementTarget(destination, serverTime);
                }
            }
        }

        public void OrderAttack(int targetOwnerClientId, float serverTime)
        {
            if (targetOwnerClientId == OwnerClientId) {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Cannot target self for attack.");
                return;
            }

            if (_targetEscadreOwnerClientIds.Add(targetOwnerClientId)) {
                Logger.Log($"[Escadre {OwnerClientId}] Added attack order on escadre of Client {targetOwnerClientId}.");
            }

            if (CurrentDestination.HasValue) {
                CurrentDestination = null;
                Logger.Log($"[Escadre {OwnerClientId}] Attack order initiated. Cancelling movement orders.");
                foreach (int shipId in _shipEntityIds) {
                    if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead) {
                        ship.SetMovementTarget(null, serverTime); 
                    }
                }
            }
        }

        public void OrderCancelAttack() 
        {
            if (TargetEscadreOwnerClientIds.Any()) {
                _targetEscadreOwnerClientIds.Clear();
                Logger.Log($"[Escadre {OwnerClientId}] Cancelling ALL attack orders.");
            } else {
                Logger.Log($"[Escadre {OwnerClientId}] No active attack orders to cancel.");
            }
        }

        public void OrderCancelAttackOn(int targetOwnerClientId)
        {
            if (_targetEscadreOwnerClientIds.Remove(targetOwnerClientId)) {
                Logger.Log($"[Escadre {OwnerClientId}] Cancelled attack order on client {targetOwnerClientId}.");
            }
        }
        
        // Signature updated, implementation replaced with NotImplementedException
        public bool RequestBuyShip(int shipDesignToBuy, Vector3 spawnPosition, float serverTime) 
        {
            Logger.LogWarning($"[Escadre {OwnerClientId}] RequestBuyShip called for design {shipDesignToBuy} at {spawnPosition}. Shop logic is intended to be external.");
            throw new NotImplementedException("Shop logic (resource check, ship creation) should be handled by a dedicated ShopManager, not Escadre directly.");
            // The external ShopManager would:
            // 1. Check resources (potentially on the Escadre or a central player resource manager).
            // 2. If successful, deduct resources.
            // 3. Create the Ship entity (e.g., new DefaultShip(_level, this, spawnPosition)).
            //    The ship's constructor adds it to the Level.
            // 4. Call this.AddShip(newShip) to register it with the Escadre.
            // 5. If the escadre has orders (CurrentDestination), apply them to the newShip with serverTime.
            // return false; // Placeholder, will be removed by the exception
        }

        public void RequestUpgradeShip(int shipId)
        {
            if (!_shipEntityIds.Contains(shipId)) { 
                Logger.LogWarning($"[Escadre {OwnerClientId}] Ship {shipId} not found for upgrade request.");
                return; 
            }
            if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead) {
                int upgradeCost = GetUpgradeCostForShip(ship); // Cost calculation can remain for now
                if (_resources >= upgradeCost) {
                    // Resource deduction and upgrade initiation would also likely move to a specialized service/manager
                    // For now, resource check is here, but PerformUpgrade is on the ship.
                    _resources -= upgradeCost;
                    Logger.Log($"[Escadre {OwnerClientId}] Upgrading Ship {shipId}. Cost: {upgradeCost}. Res: {_resources}.");
                    ship.PerformUpgrade();
                } else {
                    Logger.LogWarning($"[Escadre {OwnerClientId}] Not enough resources to upgrade Ship {shipId}. Need {upgradeCost}, Have {_resources}.");
                }
            } else {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Ship {shipId} not found in level or is dead, cannot upgrade.");
            }
        }

        internal Vector3 CalculateCenterPoint()
        {
            if (!_shipEntityIds.Any()) return Vector3.Zero; 
            Vector3 sumPositions = Vector3.Zero; int aliveShipCount = 0;
            foreach (int shipIdInList in _shipEntityIds) {
                if (_level.TryGetEntity(shipIdInList, out Entity entityInLevel) && !entityInLevel.IsDead) {
                    sumPositions += entityInLevel.Position; aliveShipCount++;
                }
            }
            return aliveShipCount > 0 ? sumPositions / aliveShipCount : Vector3.Zero; 
        }

        internal void Disband(bool silentKill = true)
        {
            Logger.Log($"[Escadre {OwnerClientId}] Disbanding (silent: {silentKill}).");
            var idsToKill = new List<int>(_shipEntityIds);
            foreach (int shipIdInList in idsToKill) {
                if (_level.TryGetEntity(shipIdInList, out Entity entityInLevel) && !entityInLevel.IsDead) {
                    entityInLevel.Kill(silentKill);
                }
            }
            _shipEntityIds.Clear();
        }
    }
}