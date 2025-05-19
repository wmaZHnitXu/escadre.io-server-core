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
            _resources = 100; // Starting resources
            // Logger.Log($"[Escadre for Client {OwnerClientId}] Created.");
        }

        public void AddResources(int amount)
        {
            if (amount <= 0) {
                // Logger.LogWarning($"[Escadre {OwnerClientId}] Attempted to add non-positive resources: {amount}.");
                return;
            }
            _resources += amount;
            // Logger.Log($"[Escadre {OwnerClientId}] Added {amount} resources. Total: {_resources}.");
        }

        private int GetUpgradeCostForShip(Ship ship)
        {
            // Placeholder for potentially more complex cost logic
            return 50;
        }

        internal void AddShip(Ship ship)
        {
            if (ship == null || ship.OwningEscadreClientId != OwnerClientId) {
                // Logger.LogWarning($"[Escadre {OwnerClientId}] Attempted to add invalid ship (null or wrong owner). Ship ID: {ship?.Id}");
                return;
            }
            if (!_shipEntityIds.Contains(ship.Id)) {
                _shipEntityIds.Add(ship.Id);
                // Logger.Log($"[Escadre {OwnerClientId}] Added Ship {ship.Id}. Total ships: {_shipEntityIds.Count}");
                // If escadre has an active movement order, apply it to the new ship
                // This requires serverTime. If not available here, ship might get its command slightly delayed
                // or rely on the next global escadre command.
                // For simplicity, assuming new ships will pick up commands on next Escadre.SetCourse or from their own proxy sync.
                // A more robust way would be for the system adding the ship to also issue its initial movement command.
                 if (_currentDestination.HasValue) {
                     // Need server time. This method is internal, so the caller (e.g. CoreComposer or RequestBuyShip)
                     // should ideally provide it or the ship will get its target on the next escadre-wide command.
                     // For now, let's assume it needs a time. If not passed, the command might be slightly off.
                     // A better way is to have SetCourse/OrderAttack update existing ships and new ships get state upon
                     // receiving their *first* movement command from their proxy.
                     // ship.SetMovementTarget(_currentDestination.Value, GetApproximateServerTime()); // Placeholder
                 }
            }
        }

        private void RemoveShip(int shipId)
        {
            if (_shipEntityIds.Remove(shipId)) {
                // Logger.Log($"[Escadre {OwnerClientId}] Removed Ship {shipId}. Total ships: {_shipEntityIds.Count}");
                if (!_shipEntityIds.Any()) {
                    // Logger.Log($"[Escadre {OwnerClientId}] All ships lost!");
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
                // Logger.Log($"[Escadre {OwnerClientId}] Setting course to {destination}. Cancelling attack orders.");
                _targetEscadreOwnerClientIds.Clear();
            } else {
                // Logger.Log($"[Escadre {OwnerClientId}] Setting course to {destination}.");
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
                // Logger.LogWarning($"[Escadre {OwnerClientId}] Cannot target self for attack.");
                return;
            }

            if (_targetEscadreOwnerClientIds.Add(targetOwnerClientId)) {
                // Logger.Log($"[Escadre {OwnerClientId}] Added attack order on escadre of Client {targetOwnerClientId}.");
            }

            if (CurrentDestination.HasValue) {
                CurrentDestination = null;
                // Logger.Log($"[Escadre {OwnerClientId}] Attack order initiated. Cancelling movement orders.");
                foreach (int shipId in _shipEntityIds) {
                    if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead) {
                        ship.SetMovementTarget(null, serverTime); // Stop ships
                    }
                }
            }
            // Ships will pick up the new target list in their UpdateAttack logic based on _targetEscadreOwnerClientIds.
            // No individual "AssignAttackOrder" is needed for ships anymore if they pull from escadre.
        }

        public void OrderCancelAttack() // No serverTime needed if it just clears targets and doesn't issue move commands
        {
            if (TargetEscadreOwnerClientIds.Any()) {
                _targetEscadreOwnerClientIds.Clear();
                // Logger.Log($"[Escadre {OwnerClientId}] Cancelling ALL attack orders.");
                // Ships will stop attacking in their UpdateAttack as the target list is empty.
                // If ships should explicitly stop moving upon attack cancel, then this would need serverTime
                // and iterate ships to call SetMovementTarget(null, serverTime).
            } else {
                // Logger.Log($"[Escadre {OwnerClientId}] No active attack orders to cancel.");
            }
        }

        public void OrderCancelAttackOn(int targetOwnerClientId)
        {
            if (_targetEscadreOwnerClientIds.Remove(targetOwnerClientId)) {
                // Logger.Log($"[Escadre {OwnerClientId}] Cancelled attack order on client {targetOwnerClientId}.");
            }
        }

        public void RequestBuyShip(int shipDesignToBuy, float serverTime) // Added serverTime for potential immediate move command
        {
            // Example using DefaultShip:
            if (shipDesignToBuy == (int)Entity.EntityTypeEnum.DefaultShip) // Placeholder check
            {
                int cost = 50; // Example cost
                if (_resources >= cost)
                {
                    _resources -= cost;
                    // Calculate spawn position relative to escadre or a fixed point
                    Vector3 spawnPosition = CalculateCenterPoint() + new Vector3(UnityEngine.Random.Range(-3f, 3f), 0, UnityEngine.Random.Range(-3f, 3f));

                    var newShip = new DefaultShip(_level, this, spawnPosition); // Constructor adds to Level
                    AddShip(newShip); // Adds to this escadre's internal list

                    // If new ships should immediately follow current escadre orders:
                    if (CurrentDestination.HasValue)
                    {
                        newShip.SetMovementTarget(CurrentDestination.Value, serverTime);
                    }
                    // else if (TargetEscadreOwnerClientIds.Any()) // No explicit "move to attack" command.
                    // {
                    //    // Ships will pick up attack in their Update.
                    // }

                    Logger.Log($"[Escadre {OwnerClientId}] Bought DefaultShip. ID (pending): {newShip.Id}. Res: {_resources}.");
                    return; // Success
                }
                else
                {
                    Logger.LogWarning($"[Escadre {OwnerClientId}] Not enough resources to buy ship. Need {cost}, Have {_resources}.");
                    return; // Failure
                }
            }
            Logger.LogWarning($"[Escadre {OwnerClientId}] RequestBuyShip for design {shipDesignToBuy} not implemented or unknown design.");
            // throw new NotImplementedException("Escadre.RequestBuyShip for specific design " + shipDesignToBuy);
        }

        public void RequestUpgradeShip(int shipId)
        {
            if (!_shipEntityIds.Contains(shipId)) { /* ... */ return; }
            if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead) {
                int upgradeCost = GetUpgradeCostForShip(ship);
                if (_resources >= upgradeCost) {
                    _resources -= upgradeCost;
                    // Logger.Log($"[Escadre {OwnerClientId}] Upgrading Ship {shipId}. Cost: {upgradeCost}. Res: {_resources}.");
                    ship.PerformUpgrade();
                } else {
                    // Logger.LogWarning($"[Escadre {OwnerClientId}] Not enough resources to upgrade Ship {shipId}. Need {upgradeCost}, Have {_resources}.");
                }
            } else {
                // Logger.LogWarning($"[Escadre {OwnerClientId}] Ship {shipId} not found/dead for upgrade.");
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
            // Logger.Log($"[Escadre {OwnerClientId}] Disbanding (silent: {silentKill}).");
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