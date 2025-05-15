// File: Scripts/Server/Core/Model/Escadre.cs
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
        // Changed to a HashSet to support multiple attack targets
        private readonly HashSet<int> _targetEscadreOwnerClientIds = new HashSet<int>();
        public IReadOnlyCollection<int> TargetEscadreOwnerClientIds => _targetEscadreOwnerClientIds;


        public int Resources { get; private set; }

        public Escadre(int ownerClientId, Level level)
        {
            OwnerClientId = ownerClientId;
            _level = level ?? throw new ArgumentNullException(nameof(level));
            Resources = 100;
            Logger.Log($"[Escadre for Client {OwnerClientId}] Created.");
        }

        /// <summary>
        /// Adds a ship to this escadre's control. Called internally by CoreComposer.
        /// </summary>
        internal void AddShip(Ship ship) // Changed to internal and takes Ship
        {
            if (ship == null || ship.OwningEscadreClientId != OwnerClientId)
            {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Attempted to add invalid ship (null or wrong owner). Ship ID: {ship?.Id}");
                return;
            }
            if (!_shipEntityIds.Contains(ship.Id))
            {
                _shipEntityIds.Add(ship.Id);
                Logger.Log($"[Escadre {OwnerClientId}] Added Ship {ship.Id}. Total ships: {_shipEntityIds.Count}");
                // If the escadre has an active movement or attack order, apply it to the new ship
                if (_currentDestination.HasValue)
                {
                     // Simplistic: all ships go to the same point.
                     // Real implementation would use formation logic.
                    ship.SetMovementTarget(_currentDestination.Value);
                }
                // Ships will pick up attack orders automatically in their UpdateAttack if _targetEscadreOwnerClientIds is populated
            }
        }

        private void RemoveShip(int shipId)
        {
            if (_shipEntityIds.Remove(shipId))
            {
                Logger.Log($"[Escadre {OwnerClientId}] Removed Ship {shipId}. Total ships: {_shipEntityIds.Count}");
                if (!_shipEntityIds.Any())
                {
                    Logger.Log($"[Escadre {OwnerClientId}] All ships lost!");
                    // ClientConnection state will be updated by higher-level logic observing Escadre state or ship count.
                }
            }
        }

        internal void HandleShipDestroyed(int shipId)
        {
            RemoveShip(shipId);
        }

        public void SetCourse(Vector2 destination)
        {
            _currentDestination = destination;
            if (_targetEscadreOwnerClientIds.Any()) // Only log cancellation if there were targets
            {
                Logger.Log($"[Escadre {OwnerClientId}] Setting course to {destination}. Cancelling attack orders.");
                _targetEscadreOwnerClientIds.Clear(); // Moving cancels all attack orders
            } else {
                Logger.Log($"[Escadre {OwnerClientId}] Setting course to {destination}.");
            }


            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    ship.SetMovementTarget(destination);
                }
            }
        }

        public void OrderAttack(int targetOwnerClientId)
        {
            if (targetOwnerClientId == OwnerClientId)
            {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Cannot target self for attack.");
                return;
            }

            if (_targetEscadreOwnerClientIds.Add(targetOwnerClientId)) // Add returns true if item was added (not already present)
            {
                 Logger.Log($"[Escadre {OwnerClientId}] Added attack order on escadre of Client {targetOwnerClientId}. Current targets: {string.Join(", ", _targetEscadreOwnerClientIds)}");
            } else {
                 Logger.Log($"[Escadre {OwnerClientId}] Already targeting escadre of Client {targetOwnerClientId}. Current targets: {string.Join(", ", _targetEscadreOwnerClientIds)}");
            }

            if(_currentDestination.HasValue) // Attacking cancels movement order
            {
                _currentDestination = null;
                Logger.Log($"[Escadre {OwnerClientId}] Attack order initiated. Cancelling any movement orders and stopping ships.");
                // Stop ships if they were moving
                foreach (int shipId in _shipEntityIds)
                {
                    if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                    {
                        ship.SetMovementTarget(null);
                    }
                }
            }
            // Ships will pick up the new target in their UpdateAttack logic. No need to iterate and call AssignAttackOrder.
        }

        public void OrderCancelAttack() // Cancels ALL attack orders
        {
            if (_targetEscadreOwnerClientIds.Any())
            {
                _targetEscadreOwnerClientIds.Clear();
                Logger.Log($"[Escadre {OwnerClientId}] Cancelling ALL attack orders.");
                // Ships will stop attacking as _targetEscadreOwnerClientIds will be empty.
            } else {
                Logger.Log($"[Escadre {OwnerClientId}] No active attack orders to cancel.");
            }
        }

        // Optional: Cancel attack on a specific escadre
        public void OrderCancelAttackOn(int targetOwnerClientId)
        {
            if (_targetEscadreOwnerClientIds.Remove(targetOwnerClientId))
            {
                Logger.Log($"[Escadre {OwnerClientId}] Cancelled attack order on escadre of Client {targetOwnerClientId}. Remaining targets: {string.Join(", ", _targetEscadreOwnerClientIds)}");
            } else {
                Logger.Log($"[Escadre {OwnerClientId}] Was not targeting escadre of Client {targetOwnerClientId}. No change to attack orders.");
            }
        }


        public void RequestBuyShip(int shipDesignToBuy) // shipDesignToBuy could now be an enum or type identifier
        {
            // TODO: Check resources, ship limits, shipyard proximity etc.
            // If successful:
            // 1. Deduct resources.
            // 2. Create new Ship entity (e.g., new Frigate(_level, this, spawnPosition)).
            //    Level.AddEntity() is called by Entity constructor.
            // 3. Call this.AddShip(newShipInstance).
            Logger.Log($"[Escadre {OwnerClientId}] RequestBuyShip called for design {shipDesignToBuy}. (NotImplemented)");

            // Example for a DefaultShip
            // if (Resources >= 50) // Cost of DefaultShip
            // {
            //     Resources -= 50;
            //     Vector3 spawnPosition = CalculateCenterPoint() + new Vector3(UnityEngine.Random.Range(-5f, 5f), 0, UnityEngine.Random.Range(-5f, 5f)); // Offset from escadre center
            //     var newShip = new DefaultShip(_level, this, spawnPosition); // Assuming DefaultShip exists
            //     // AddShip(newShip); // Ship constructor calls _level.AddEntity. Escadre.AddShip is called by CoreComposer or similar post-creation.
            //                               // Actually, better for RequestBuyShip to fully manage the ship creation and addition to escadre.
            //                               // The ship's constructor will add it to the _level.
            //                               // Then this method should call this.AddShip(newShip).
            //     Logger.Log($"[Escadre {OwnerClientId}] Bought ship. New ship ID will be {newShip.Id} (once processed by Level).");
            // } else {
            //     Logger.LogWarning($"[Escadre {OwnerClientId}] Not enough resources to buy ship design {shipDesignToBuy}.");
            // }
            throw new NotImplementedException("Escadre.RequestBuyShip");
        }

        public void RequestUpgradeShip(int shipId)
        {
            if (!_shipEntityIds.Contains(shipId))
            {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Attempted to upgrade ship {shipId} not in escadre.");
                return;
            }
            if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
            {
                // TODO: Check resources, upgrade paths, etc.
                // Example:
                // int upgradeCost = GetUpgradeCost(ship.EntityType); // You'd need a way to get cost
                // if (Resources >= upgradeCost) {
                //    Resources -= upgradeCost;
                //    Logger.Log($"[Escadre {OwnerClientId}] Requesting upgrade for Ship {shipId}. Deducted {upgradeCost} resources.");
                //    ship.PerformUpgrade();
                // } else {
                //    Logger.LogWarning($"[Escadre {OwnerClientId}] Not enough resources to upgrade Ship {shipId}.");
                // }
                ship.PerformUpgrade(); // Ship handles its own upgrade logic
            }
            else
            {
                 Logger.LogWarning($"[Escadre {OwnerClientId}] Ship {shipId} not found or dead, cannot upgrade.");
            }
        }

        internal Vector3 CalculateCenterPoint()
        {
            if (!_shipEntityIds.Any()) return Vector3.Zero;

            Vector3 sumPositions = Vector3.Zero;
            int aliveShipCount = 0;

            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && !entity.IsDead)
                {
                    sumPositions += entity.Position;
                    aliveShipCount++;
                }
            }
            return aliveShipCount > 0 ? sumPositions / aliveShipCount : Vector3.Zero;
        }

        internal void Disband(bool silentKill = true)
        {
             Logger.Log($"[Escadre {OwnerClientId}] Disbanding (silent: {silentKill}). Killing all ships.");
             var idsToKill = new List<int>(_shipEntityIds);
             foreach (int shipId in idsToKill)
             {
                 if (_level.TryGetEntity(shipId, out Entity entity) && !entity.IsDead)
                 {
                     entity.Kill(silentKill);
                 }
             }
             _shipEntityIds.Clear();
        }
    }
}