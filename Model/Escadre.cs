// File: Core/Model/Escadre.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    public class Escadre : Entity // Inherit from Entity
    {
        public override EntityTypeEnum EntityType => EntityTypeEnum.Escadre;

        public int OwnerClientId { get; }
        public string Nickname { get; private set; }

        private readonly List<int> _shipEntityIds = new List<int>();
        public IReadOnlyList<int> ShipEntityIds => _shipEntityIds.AsReadOnly();

        private Vector2? _currentDestination; // User-commanded destination for the fleet
        public Vector2? CurrentDestination { get => _currentDestination; private set => _currentDestination = value; }

        private Vector3 _fleetCommandTargetPoint; // The point the fleet as a whole is trying to reach or orient towards
        private bool _isFleetMovingToTarget;

        private readonly HashSet<int> _targetEscadreEntityIds = new HashSet<int>();
        public IReadOnlyCollection<int> TargetEscadreEntityIds => _targetEscadreEntityIds;


        private int _resources;
        public int Resources { get => _resources; private set => _resources = value; }
        
        public event Action<int> OnResourcesChanged; 
        public event Action<Formation> OnFormationChanged; 

        public Formation CurrentFormation { get; private set; }
        private bool _isDisbanding = false; 


        public Escadre(Level level, int ownerClientId, string nickname, Vector3 initialPosition) : base(level)
        {
            OwnerClientId = ownerClientId;
            Nickname = nickname ?? $"Escadre_{OwnerClientId}";
            
            // Initial position of the Escadre entity itself is set by the constructor
            // but will be dynamically updated to the average of its ships.
            // For the very first frame before any ships are added, it will be this initialPosition.
            // Or, if ships are added immediately, it will update quickly.
            this.Position = initialPosition; 
            this.Rotation = Quaternion.Identity; 
            _fleetCommandTargetPoint = initialPosition; // Initially, command target is current position
            _isFleetMovingToTarget = false;

            _resources = 1000; 
            CurrentFormation = new Formation(OwnerClientId, new DefaultFormationValidationStrategy());
            CurrentFormation.OnFormationLayoutChanged += HandleInternalFormationLayoutChange;
        }

        private void HandleInternalFormationLayoutChange()
        {
            OnFormationChanged?.Invoke(CurrentFormation);
            if (!_isDisbanding) UpdateShipMovementTargets(_level.CurrentTime);
        }

        public void AddResources(int amount)
        {
            if (amount <= 0) return;
            _resources += amount;
            OnResourcesChanged?.Invoke(_resources);
        }

        public bool DeductResources(int amount)
        {
            if (amount <= 0) return true; 
            if (_resources >= amount)
            {
                _resources -= amount;
                OnResourcesChanged?.Invoke(_resources);
                return true;
            }
            return false;
        }

        public int GetUpgradeCostForShip(Ship ship)
        {
            return 50; // Placeholder
        }

        internal void AddShip(Ship ship, Vector2? initialFormationOffset = null)
        {
            if (_isDisbanding) return;
            if (ship == null || ship.OwningEscadreClientId != OwnerClientId) {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Attempted to add invalid ship. Ship ID: {ship?.Id}");
                return;
            }
            if (!_shipEntityIds.Contains(ship.Id)) {
                _shipEntityIds.Add(ship.Id);
                bool addedToFormation;
                Vector2 finalOffset;
                if (initialFormationOffset.HasValue)
                {
                    addedToFormation = CurrentFormation.TryAddShip(ship.Id, initialFormationOffset.Value, out string reason);
                    finalOffset = initialFormationOffset.Value;
                    if(!addedToFormation) Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Failed to add ship {ship.Id} to formation at preferred offset {initialFormationOffset.Value}: {reason}");
                }
                else
                {
                    addedToFormation = CurrentFormation.AssignShipToAutoSlot(ship.Id, out finalOffset);
                     if(!addedToFormation) Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Failed to auto-assign ship {ship.Id} to formation.");
                }
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Added Ship {ship.Id} at formation offset {finalOffset}. Total ships: {_shipEntityIds.Count}");
                // Initial position of escadre might need an immediate update if this is the first ship
                if (_shipEntityIds.Count == 1)
                {
                    _fleetCommandTargetPoint = ship.Position; // Center command on the first ship initially
                }
            }
        }

        private void RemoveShip(int shipId)
        {
            if (_isDisbanding) return;
            if (_shipEntityIds.Remove(shipId)) {
                CurrentFormation.RemoveShip(shipId);
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Removed Ship {shipId}. Total ships: {_shipEntityIds.Count}");
                if (!_shipEntityIds.Any()) {
                    Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] All ships lost! Escadre might become static or be destroyed.");
                    _isFleetMovingToTarget = false; // Stop commanding movement if no ships
                }
            }
        }

        internal void HandleShipDestroyed(int shipId)
        {
            RemoveShip(shipId);
            if (!_shipEntityIds.Any() && !IsDead) 
            {
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] All ships lost! Initiating Escadre Kill().");
                this.Kill(); 
            }
        }

        public void SetCourse(Vector2 destination, float serverTime)
        {
            if (_isDisbanding || IsDead) return;
            CurrentDestination = destination;
            _fleetCommandTargetPoint = new Vector3(destination.X, this.Position.Y, destination.Y); // Y is current avg, will adjust
            _isFleetMovingToTarget = true;

            if (TargetEscadreEntityIds.Any()) { 
                _targetEscadreEntityIds.Clear();
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Setting course to {destination}. Cancelling attack orders.");
            } else {
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Setting course to {destination}.");
            } 
            UpdateShipMovementTargets(serverTime);
        }
        
        public void OrderAttackEscadreEntity(int targetEscadreEntityId, float serverTime)
        {
            if (_isDisbanding || IsDead) return;
            if (targetEscadreEntityId == this.Id) {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Cannot target self for attack.");
                return;
            }

            if (_level.TryGetEntity(targetEscadreEntityId, out Entity targetEntity) && targetEntity is Escadre targetEscadre)
            {
                if (_targetEscadreEntityIds.Add(targetEscadreEntityId)) {
                     Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Added attack order on escadre entity {targetEscadreEntityId} (Owner {targetEscadre.OwnerClientId}).");
                }
                _fleetCommandTargetPoint = targetEscadre.Position; // Initial command point
                _isFleetMovingToTarget = true;
                CurrentDestination = null; // Clear fixed destination
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Attack order on entity {targetEscadreEntityId} initiated. Cancelling fixed movement orders.");
                UpdateShipMovementTargets(serverTime);
            }
            else
            {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] OrderAttackEscadreEntity: Target Escadre Entity ID {targetEscadreEntityId} not found or not an Escadre.");
            }
        }

        public void OrderCancelAttack() 
        {
            if (_isDisbanding || IsDead) return;
            bool hadTargets = TargetEscadreEntityIds.Any();
            if (hadTargets) {
                _targetEscadreEntityIds.Clear();
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Cancelling ALL attack orders.");
                if (!CurrentDestination.HasValue) 
                {
                    _isFleetMovingToTarget = false;
                }
                UpdateShipMovementTargets(_level.CurrentTime); 
            } else {
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] No active attack orders to cancel.");
            }
        }
        
        public bool RequestSetFormation(List<Tuple<int, Vector2>> newFormationSlots, float serverTime)
        {
            if (_isDisbanding || IsDead) return false;
            var proposedSlots = new List<FormationSlot>();
            bool allShipsFound = true;
            foreach (var slotData in newFormationSlots)
            {
                if (!_shipEntityIds.Contains(slotData.Item1))
                {
                    Logger.LogWarning($"[Escadre {Id}] RequestSetFormation: Ship ID {slotData.Item1} not in escadre.");
                    allShipsFound = false;
                    break;
                }
                proposedSlots.Add(new FormationSlot(slotData.Item2, slotData.Item1));
            }

            if (!allShipsFound) return false; 

            foreach(var existingShipId in _shipEntityIds)
            {
                if (!newFormationSlots.Any(s => s.Item1 == existingShipId))
                {
                    Logger.LogWarning($"[Escadre {Id}] RequestSetFormation: Existing ship ID {existingShipId} is missing. Request rejected.");
                    return false; 
                }
            }

            if (!CurrentFormation.ValidationStrategy.IsValidFormation(proposedSlots, out string reason))
            {
                Logger.LogWarning($"[Escadre {Id}] Proposed formation is invalid: {reason}");
                return false;
            }

            bool changed = false;
            foreach (var slotData in newFormationSlots)
            {
                if (CurrentFormation.TrySetShipRelativeOffset(slotData.Item1, slotData.Item2, out _))
                {
                    changed = true; 
                }
            }
            
            if (changed)
            {
                Logger.Log($"[Escadre {Id}] Formation updated successfully via request.");
                // UpdateShipMovementTargets is called by HandleInternalFormationLayoutChange
                return true;
            }
            return false; 
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime); // Handles base Entity floating behavior if any (Escadre typically doesn't float itself)

            if (IsDead || _isDisbanding) return;

            // 1. Update _fleetCommandTargetPoint based on current orders
            if (TargetEscadreEntityIds.Any())
            {
                int primaryTargetId = TargetEscadreEntityIds.First(); // Simple: focus on first target
                if (_level.TryGetEntity(primaryTargetId, out Entity targetEntity) && targetEntity is Escadre enemyEscadre && !enemyEscadre.IsDead)
                {
                    _fleetCommandTargetPoint = enemyEscadre.Position;
                    _isFleetMovingToTarget = true;
                }
                else // Target lost or dead
                {
                    _targetEscadreEntityIds.Remove(primaryTargetId);
                    if (!_targetEscadreEntityIds.Any()) _isFleetMovingToTarget = false;
                }
            }
            else if (CurrentDestination.HasValue)
            {
                Vector3 currentFleetCommandTarget3D = new Vector3(CurrentDestination.Value.X, this.Position.Y, CurrentDestination.Value.Y);
                 // Check if reached destination
                if ((currentFleetCommandTarget3D - _fleetCommandTargetPoint).SqrMagnitude < 1.0f * 1.0f) // If close enough to target
                {
                     // More precise check could be against average ship position
                     Vector3 avgPos = CalculateAverageShipPosition();
                     if ((new Vector2(avgPos.X, avgPos.Z) - CurrentDestination.Value).SqrMagnitude < 2.0f * 2.0f)
                     {
                        CurrentDestination = null;
                        _isFleetMovingToTarget = false;
                        Logger.Log($"[Escadre {Id}] Reached destination. Stopping commanded movement.");
                     } else {
                        _fleetCommandTargetPoint = currentFleetCommandTarget3D; // Update Y if needed
                        _isFleetMovingToTarget = true;
                     }
                } else {
                     _fleetCommandTargetPoint = currentFleetCommandTarget3D;
                     _isFleetMovingToTarget = true;
                }
            }
            else
            {
                _isFleetMovingToTarget = false; // No explicit orders
            }

            // 2. Calculate current average ship position and set Escadre.Position
            Vector3 newAveragePosition = CalculateAverageShipPosition();
            if (this.Position != newAveragePosition)
            {
                this.Position = newAveragePosition; // This updates the base Entity._position
            }
            
            // 3. Update Escadre.Rotation (general fleet orientation)
            if (_isFleetMovingToTarget && (_fleetCommandTargetPoint - this.Position).SqrMagnitude > 0.1f)
            {
                Vector3 directionToCommandTarget = (_fleetCommandTargetPoint - this.Position).Normalized;
                if (directionToCommandTarget.SqrMagnitude > Vector3.Epsilon)
                {
                    this.Rotation = Quaternion.LookRotation(directionToCommandTarget, Vector3.Up);
                }
            }
            // else: maintain current rotation or gradually return to a default if desired. For now, maintain.

            // 4. Update individual ship movement targets
            UpdateShipMovementTargets(_level.CurrentTime);
        }

        private Vector3 CalculateAverageShipPosition()
        {
            if (!_shipEntityIds.Any())
            {
                // If no ships, Escadre position might be its last known average, or _fleetCommandTargetPoint if stationary
                return _isFleetMovingToTarget ? _fleetCommandTargetPoint : this.Position;
            }

            Vector3 sumPositions = Vector3.Zero;
            int aliveShipCount = 0;
            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    sumPositions += ship.Position;
                    aliveShipCount++;
                }
            }

            if (aliveShipCount > 0)
            {
                return sumPositions / aliveShipCount;
            }
            return _isFleetMovingToTarget ? _fleetCommandTargetPoint : this.Position; // Fallback if all listed ships are dead/gone
        }


        internal void UpdateShipMovementTargets(float serverTime)
        {
            if (_isDisbanding || IsDead) return;

            Vector3 referencePointForFormation; // The point around which ships should form up.
            if (_isFleetMovingToTarget)
            {
                referencePointForFormation = _fleetCommandTargetPoint;
            }
            else // Not moving to a specific target, ships should hold formation around current Escadre (average) position
            {
                referencePointForFormation = this.Position; 
            }

            Quaternion fleetOrientation = this.Rotation; // Use the Escadre entity's current (commanded) rotation

            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    Vector2 relativeOffset2D = CurrentFormation.GetShipRelativeOffset(shipId);
                    Vector3 relativeOffset3D = new Vector3(relativeOffset2D.X, 0, relativeOffset2D.Y);
                    Vector3 worldOffsetFromReference = fleetOrientation * relativeOffset3D;
                    
                    Vector3 targetShipWorldPosition = referencePointForFormation + worldOffsetFromReference;
                    ship.SetMovementTarget(new Vector2(targetShipWorldPosition.X, targetShipWorldPosition.Z), serverTime);
                }
            }
        }
        
        public Vector3 CalculateGeometricCenterOfShips() // This is essentially CalculateAverageShipPosition
        {
            return CalculateAverageShipPosition();
        }

        internal void Disband(bool silentKillShips = true)
        {
            _isDisbanding = true; 
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Disbanding (silentKillShips: {silentKillShips}).");
            
            CurrentDestination = null;
            _targetEscadreEntityIds.Clear();
            _isFleetMovingToTarget = false;

            var idsToKill = new List<int>(_shipEntityIds); 
            foreach (int shipIdInList in idsToKill) {
                if (_level.TryGetEntity(shipIdInList, out Entity entityInLevel) && !entityInLevel.IsDead)
                {
                    entityInLevel.Kill(silentKillShips); 
                }
            }
            _shipEntityIds.Clear(); 
            CurrentFormation.RemoveAllShips(); 
            _isDisbanding = false; 
        }

        protected override void Death()
        {
            base.Death(); 
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Performing Death(). Disbanding ships non-silently.");
            Disband(false); 
        }

        protected override void ObligatoryOnRemove()
        {
            base.ObligatoryOnRemove();
             Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Performing ObligatoryOnRemove().");
            if (!_isDisbanding) 
            {
                Disband(true); 
            }
            OnResourcesChanged = null;
            OnFormationChanged = null;
            if (CurrentFormation != null)
            {
                CurrentFormation.OnFormationLayoutChanged -= HandleInternalFormationLayoutChange;
                CurrentFormation.RemoveAllShips(); 
            }
        }
    }
}