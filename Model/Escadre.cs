// File: Scripts/Server/Core/Model/Escadre.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    /// <summary>
    /// Represents a player's escadre (squadron) of ships.
    /// Manages the ships, resources, and high-level orders.
    /// Does NOT inherit from Entity. Its lifecycle is tied to ClientConnection.
    /// </summary>
    public class Escadre
    {
        public int OwnerClientId { get; } // ID of the client owning this escadre
        private readonly Level _level;     // Reference to the level for entity interactions

        private readonly List<int> _shipEntityIds = new List<int>();
        public IReadOnlyList<int> ShipEntityIds => _shipEntityIds.AsReadOnly();

        private Vector2? _currentDestination;
        private int? _currentTargetEscadreOwnerClientId; // The ClientID of the escadre to target

        public int Resources { get; private set; } // Generic "gear" resource

        public Escadre(int ownerClientId, Level level)
        {
            OwnerClientId = ownerClientId;
            _level = level ?? throw new ArgumentNullException(nameof(level));
            Resources = 100; // Starting resources example
            Logger.Log($"[Escadre for Client {OwnerClientId}] Created.");
        }

        /// <summary>
        /// Adds a ship to this escadre's control. Called internally.
        /// </summary>
        private void AddShip(Ship ship)
        {
            if (ship == null || ship.OwningEscadreClientId != OwnerClientId)
            {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Attempted to add invalid ship (null or wrong owner).");
                return;
            }
            if (!_shipEntityIds.Contains(ship.Id))
            {
                _shipEntityIds.Add(ship.Id);
                Logger.Log($"[Escadre {OwnerClientId}] Added Ship {ship.Id}. Total ships: {_shipEntityIds.Count}");
            }
        }

        /// <summary>
        /// Removes a ship from this escadre's control. Called internally.
        /// </summary>
        private void RemoveShip(int shipId)
        {
            if (_shipEntityIds.Remove(shipId))
            {
                Logger.Log($"[Escadre {OwnerClientId}] Removed Ship {shipId}. Total ships: {_shipEntityIds.Count}");
                if (!_shipEntityIds.Any())
                {
                    Logger.Log($"[Escadre {OwnerClientId}] All ships lost!");
                    // Notify ClientConnection or Level that this escadre is effectively destroyed
                    // This logic will be handled via ClientConnection state changes.
                }
            }
        }

        /// <summary>
        /// Called by a Ship when it is destroyed.
        /// </summary>
        internal void HandleShipDestroyed(int shipId)
        {
            RemoveShip(shipId);
        }


        // --- Public Use Case Methods (Called by ClientConnection) ---

        /// <summary>
        /// Sets a new course (destination) for the entire escadre.
        /// </summary>
        public void SetCourse(Vector2 destination)
        {
            _currentDestination = destination;
            _currentTargetEscadreOwnerClientId = null; // Moving cancels attack order
            Logger.Log($"[Escadre {OwnerClientId}] Setting course to {destination}. Cancelling any attack orders.");

            // TODO: Distribute movement targets to individual ships based on formation/destination
            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    // Simplistic: all ships go to the same point.
                    // Real implementation would use formation logic.
                    ship.SetMovementTarget(destination);
                }
            }
        }

        /// <summary>
        /// Orders the escadre to attack ships belonging to another client's escadre.
        /// </summary>
        public void OrderAttack(int targetOwnerClientId)
        {
            if (targetOwnerClientId == OwnerClientId)
            {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Cannot target self for attack.");
                return;
            }

            _currentTargetEscadreOwnerClientId = targetOwnerClientId;
            _currentDestination = null; // Attacking cancels movement order
            Logger.Log($"[Escadre {OwnerClientId}] Ordering attack on escadre of Client {targetOwnerClientId}. Cancelling any movement orders.");

            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    ship.AssignAttackOrder(targetOwnerClientId);
                }
            }
        }

        /// <summary>
        /// Cancels any current attack order for all ships in the escadre.
        /// </summary>
        public void OrderCancelAttack()
        {
            _currentTargetEscadreOwnerClientId = null;
            Logger.Log($"[Escadre {OwnerClientId}] Cancelling attack order.");
            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    ship.AssignAttackOrder(null); // Pass null to cancel
                }
            }
        }

        /// <summary>
        /// Requests to buy a new ship for the escadre. (Placeholder)
        /// </summary>
        public void RequestBuyShip(int shipDesignToBuy)
        {
            // TODO: Check resources, ship limits, shipyard proximity etc.
            // If successful:
            // 1. Deduct resources.
            // 2. Create new Ship entity (_level.AddEntity(new Ship(...))).
            // 3. Call private AddShip(newShipInstance).
            Logger.Log($"[Escadre {OwnerClientId}] RequestBuyShip called for design {shipDesignToBuy}. (NotImplemented)");
            throw new NotImplementedException("Escadre.RequestBuyShip");
        }

        /// <summary>
        /// Requests to upgrade an existing ship in the escadre.
        /// </summary>
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
                Logger.Log($"[Escadre {OwnerClientId}] Requesting upgrade for Ship {shipId}.");
                ship.PerformUpgrade(); // Ship handles its own upgrade logic
            }
            else
            {
                 Logger.LogWarning($"[Escadre {OwnerClientId}] Ship {shipId} not found or dead, cannot upgrade.");
            }
        }

        /// <summary>
        /// Calculates the average position of all living ships in the escadre.
        /// Used by ClientConnection for IClientView.Position.
        /// </summary>
        internal Vector3 CalculateCenterPoint()
        {
            if (!_shipEntityIds.Any()) return Vector3.Zero; // Or some default spawn point

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

        /// <summary>
        /// Kills all ships in the escadre silently. Used when client disconnects or escadre is disbanded.
        /// </summary>
        internal void Disband(bool silentKill = true)
        {
             Logger.Log($"[Escadre {OwnerClientId}] Disbanding (silent: {silentKill}). Killing all ships.");
             // Iterate a copy as Kill() might modify the _shipEntityIds list via HandleShipDestroyed
             var idsToKill = new List<int>(_shipEntityIds);
             foreach (int shipId in idsToKill)
             {
                 if (_level.TryGetEntity(shipId, out Entity entity) && !entity.IsDead)
                 {
                     entity.Kill(silentKill);
                 }
             }
             _shipEntityIds.Clear(); // Ensure list is cleared even if some ships weren't found/already dead
        }
    }
}