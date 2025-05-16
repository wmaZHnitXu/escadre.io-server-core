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

        // Modified for cleaner public access for debugging and internal use
        private Vector2? _currentDestination;
        public Vector2? CurrentDestination { get => _currentDestination; private set => _currentDestination = value; }


        private readonly HashSet<int> _targetEscadreOwnerClientIds = new HashSet<int>();
        public IReadOnlyCollection<int> TargetEscadreOwnerClientIds => _targetEscadreOwnerClientIds;


        private int _resources; // Backing field for Resources
        public int Resources { get => _resources; private set => _resources = value; }


        public Escadre(int ownerClientId, Level level)
        {
            OwnerClientId = ownerClientId;
            _level = level ?? throw new ArgumentNullException(nameof(level));
            _resources = 100;
            Logger.Log($"[Escadre for Client {OwnerClientId}] Created.");
        }

        // Added method to allow external systems (e.g., crate collection) to give resources
        public void AddResources(int amount)
        {
            if (amount <= 0)
            {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Attempted to add non-positive resources: {amount}. Ignored.");
                return;
            }
            _resources += amount;
            Logger.Log($"[Escadre {OwnerClientId}] Added {amount} resources. Total: {_resources}.");
        }

        // Helper for upgrade cost, could be more complex
        private int GetUpgradeCostForShip(Ship ship)
        {
            // Example: cost could depend on ship type or current upgrade level
            return 50; // Placeholder cost
        }


        internal void AddShip(Ship ship)
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
                if (_currentDestination.HasValue) // Apply current escadre destination to new ship
                {
                    ship.SetMovementTarget(_currentDestination.Value);
                }
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
                    // State change to Destroyed or Spectating for ClientConnection would be handled
                    // by a system observing the Escadre or its ship count.
                }
            }
        }

        internal void HandleShipDestroyed(int shipId)
        {
            RemoveShip(shipId);
        }

        public void SetCourse(Vector2 destination)
        {
            CurrentDestination = destination; // Use the property setter
            if (_targetEscadreOwnerClientIds.Any())
            {
                Logger.Log($"[Escadre {OwnerClientId}] Setting course to {destination}. Cancelling attack orders.");
                _targetEscadreOwnerClientIds.Clear();
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

            if (_targetEscadreOwnerClientIds.Add(targetOwnerClientId))
            {
                 Logger.Log($"[Escadre {OwnerClientId}] Added attack order on escadre of Client {targetOwnerClientId}. Current targets: {string.Join(", ", _targetEscadreOwnerClientIds)}");
            } else {
                 Logger.Log($"[Escadre {OwnerClientId}] Already targeting escadre of Client {targetOwnerClientId}. Current targets: {string.Join(", ", _targetEscadreOwnerClientIds)}");
            }

            if(CurrentDestination.HasValue) // Use property getter
            {
                CurrentDestination = null; // Attacking cancels movement order
                Logger.Log($"[Escadre {OwnerClientId}] Attack order initiated. Cancelling any movement orders and stopping ships.");
                foreach (int shipId in _shipEntityIds)
                {
                    if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                    {
                        ship.SetMovementTarget(null); // Stop individual ships
                    }
                }
            }
        }

        public void OrderCancelAttack()
        {
            if (_targetEscadreOwnerClientIds.Any())
            {
                _targetEscadreOwnerClientIds.Clear();
                Logger.Log($"[Escadre {OwnerClientId}] Cancelling ALL attack orders.");
            } else {
                Logger.Log($"[Escadre {OwnerClientId}] No active attack orders to cancel.");
            }
        }

        public void OrderCancelAttackOn(int targetOwnerClientId)
        {
            if (_targetEscadreOwnerClientIds.Remove(targetOwnerClientId))
            {
                Logger.Log($"[Escadre {OwnerClientId}] Cancelled attack order on escadre of Client {targetOwnerClientId}. Remaining targets: {string.Join(", ", _targetEscadreOwnerClientIds)}");
            } else {
                Logger.Log($"[Escadre {OwnerClientId}] Was not targeting escadre of Client {targetOwnerClientId}. No change to attack orders.");
            }
        }


        public void RequestBuyShip(int shipDesignToBuy) // shipDesignToBuy could be an enum or type ID
        {
            // Example with DefaultShip
            // if (shipDesignToBuy == (int)Entity.EntityTypeEnum.DefaultShip) // Assuming an ID mapping
            // {
            //     int cost = 50; // Cost for DefaultShip
            //     if (_resources >= cost)
            //     {
            //         _resources -= cost;
            //         Vector3 spawnPosition = CalculateCenterPoint() + new Vector3(UnityEngine.Random.Range(-5f, 5f), 0, UnityEngine.Random.Range(-5f, 5f));
            //         var newShip = new DefaultShip(_level, this, spawnPosition);
            //         // AddShip(newShip); // The CoreComposer currently handles calling AddShip after creating the initial ship.
            //                              // For subsequent buys, this method should create and then call AddShip.
            //         Logger.Log($"[Escadre {OwnerClientId}] Bought DefaultShip. Remaining Res: {_resources}. Ship ID pending.");
            //         // The newShip will be added to the Level via its constructor, and its ID assigned.
            //         // Then this.AddShip(newShip) would add its ID to _shipEntityIds.
            //         // This part needs careful orchestration if buy requests come from client commands.
            //     } else {
            //          Logger.LogWarning($"[Escadre {OwnerClientId}] Not enough resources to buy ship. Need {cost}, Have {_resources}.");
            //     }
            // } else {
            //      Logger.LogWarning($"[Escadre {OwnerClientId}] Unknown ship design to buy: {shipDesignToBuy}.");
            // }
            Logger.Log($"[Escadre {OwnerClientId}] RequestBuyShip called for design {shipDesignToBuy}. (NotImplemented)");
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
                int upgradeCost = GetUpgradeCostForShip(ship); // Get cost based on ship or upgrade type

                if (_resources >= upgradeCost)
                {
                    _resources -= upgradeCost; // Deduct resources from escadre
                    Logger.Log($"[Escadre {OwnerClientId}] Upgrading Ship {shipId}. Cost: {upgradeCost}. Remaining Res: {_resources}.");
                    ship.PerformUpgrade(); // Tell the ship to apply its upgrade
                }
                else
                {
                    Logger.LogWarning($"[Escadre {OwnerClientId}] Not enough resources to upgrade Ship {shipId}. Need: {upgradeCost}, Have: {_resources}.");
                }
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

            foreach (int shipIdInList in _shipEntityIds) // Use a distinct loop variable name
            {
                if (_level.TryGetEntity(shipIdInList, out Entity entityInLevel) && !entityInLevel.IsDead) // Use a distinct loop variable name
                {
                    sumPositions += entityInLevel.Position;
                    aliveShipCount++;
                }
            }

            return aliveShipCount > 0 ? sumPositions / aliveShipCount : Vector3.Zero;
        }

        internal void Disband(bool silentKill = true)
        {
             Logger.Log($"[Escadre {OwnerClientId}] Disbanding (silent: {silentKill}). Killing all ships.");
             var idsToKill = new List<int>(_shipEntityIds);
             foreach (int shipIdInList in idsToKill) // Use a distinct loop variable name
             {
                 if (_level.TryGetEntity(shipIdInList, out Entity entityInLevel) && !entityInLevel.IsDead) // Use a distinct loop variable name
                 {
                     entityInLevel.Kill(silentKill);
                 }
             }
             _shipEntityIds.Clear();
        }
    }
}