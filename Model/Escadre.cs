// File: Core/Model/Escadre.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    public class Escadre : Entity 
    {
        public override EntityTypeEnum EntityType => EntityTypeEnum.Escadre;

        public int OwnerClientId { get; }
        public string Nickname { get; private set; }

        private readonly List<int> _shipEntityIds = new List<int>();
        public IReadOnlyList<int> ShipEntityIds => _shipEntityIds.AsReadOnly();

        private Vector2? _currentDestination; 
        public Vector2? CurrentDestination { get => _currentDestination; private set => _currentDestination = value; }

        private Vector3 _fleetCommandTargetPoint; 
        private bool _isFleetMovingToTarget;
        private bool _wasPreviouslyMovingToTarget = false; // To detect transition to holding

        private readonly HashSet<int> _targetEscadreEntityIds = new HashSet<int>();
        public IReadOnlyCollection<int> TargetEscadreEntityIds => _targetEscadreEntityIds;

        private int _resources;
        public int Resources { get => _resources; private set => _resources = value; }
        
        public event Action<int> OnResourcesChanged; 
        public event Action<Formation> OnFormationChanged; 
        public event Action<float> CurrentFleetSpeedChanged; 

        public Formation CurrentFormation { get; private set; }
        private bool _isDisbanding = false; 

        public float FleetMaxSpeed { get; protected set; }
        private float _currentFleetSpeed; 
        public float CurrentFleetSpeed 
        { 
            get => _currentFleetSpeed;
            protected set
            {
                float previousValue = _currentFleetSpeed;
                _currentFleetSpeed = value;
                if (Math.Abs(previousValue - _currentFleetSpeed) > 0.01f || (previousValue != 0 && _currentFleetSpeed == 0))
                {
                    CurrentFleetSpeedChanged?.Invoke(_currentFleetSpeed);
                }
            }
        }
        public float FormationIntegrityFactor { get; set; } 
        public float FleetAcceleration { get; protected set; } 
        public float MaxFormationSpreadRadius { get; protected set; } 

        public Escadre(Level level, int ownerClientId, string nickname, Vector3 initialPosition) : base(level)
        {
            OwnerClientId = ownerClientId;
            Nickname = nickname ?? $"Escadre_{OwnerClientId}";
            
            this.Position = initialPosition; 
            this.Rotation = Quaternion.Identity; 
            _fleetCommandTargetPoint = initialPosition; // Initially, hold at current position
            _isFleetMovingToTarget = false;
            _wasPreviouslyMovingToTarget = false;

            _resources = 1000; 
            CurrentFormation = new Formation(OwnerClientId, new DefaultFormationValidationStrategy());
            CurrentFormation.OnFormationLayoutChanged += HandleInternalFormationLayoutChange;

            FleetMaxSpeed = 3.5f; 
            _currentFleetSpeed = 0f; 
            FormationIntegrityFactor = 1.0f;
            FleetAcceleration = 1.0f; 
            MaxFormationSpreadRadius = 35f; 
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
                if (_shipEntityIds.Count == 1 && !_isFleetMovingToTarget) // If first ship and not already moving
                {
                    _fleetCommandTargetPoint = this.Position; // Anchor to current (average) position
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
                    _isFleetMovingToTarget = false; 
                    CurrentFleetSpeed = 0f;
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
            if (_isDisbanding || IsDead || !_shipEntityIds.Any()) return;
            CurrentDestination = destination;
            _isFleetMovingToTarget = true; 

            if (TargetEscadreEntityIds.Any()) { 
                _targetEscadreEntityIds.Clear();
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Setting course to {destination}. Cancelling attack orders.");
            } else {
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Setting course to {destination}.");
            } 
            // _fleetCommandTargetPoint will be updated in Update() based on CurrentDestination
            UpdateShipMovementTargets(serverTime);
        }
        
        public void OrderAttackEscadreEntity(int targetEscadreEntityId, float serverTime)
        {
            if (_isDisbanding || IsDead || !_shipEntityIds.Any()) return;
            if (targetEscadreEntityId == this.Id) {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Cannot target self for attack.");
                return;
            }

            if (_level.TryGetEntity(targetEscadreEntityId, out Entity targetEntity) && targetEntity is Escadre targetEscadre)
            {
                if (_targetEscadreEntityIds.Add(targetEscadreEntityId)) {
                     Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Added attack order on escadre entity {targetEscadreEntityId} (Owner {targetEscadre.OwnerClientId}).");
                }
                _isFleetMovingToTarget = true; 
                CurrentDestination = null; 
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Attack order on entity {targetEscadreEntityId} initiated. Cancelling fixed movement orders.");
                // _fleetCommandTargetPoint will be updated in Update() based on targetEscadre.Position
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
                // _fleetCommandTargetPoint will be set to this.Position if no CurrentDestination after this.
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
                return true;
            }
            return false; 
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime); 

            if (IsDead || _isDisbanding || !_shipEntityIds.Any())
            {
                CurrentFleetSpeed = 0f; 
                return;
            }
            
            // --- Determine current movement intention and update _fleetCommandTargetPoint ---
            bool stillActivelyCommandedToMove = false;
            if (TargetEscadreEntityIds.Any())
            {
                int primaryTargetId = TargetEscadreEntityIds.First(); 
                if (_level.TryGetEntity(primaryTargetId, out Entity targetEntity) && targetEntity is Escadre enemyEscadre && !enemyEscadre.IsDead)
                {
                    _fleetCommandTargetPoint = enemyEscadre.Position; 
                    stillActivelyCommandedToMove = true;
                }
                else 
                {
                    _targetEscadreEntityIds.Remove(primaryTargetId);
                    if (!_targetEscadreEntityIds.Any() && CurrentDestination.HasValue)
                    {
                         // Fallback to CurrentDestination if primary attack target lost
                        _fleetCommandTargetPoint = new Vector3(CurrentDestination.Value.X, this.Position.Y, CurrentDestination.Value.Y);
                        stillActivelyCommandedToMove = true;
                    } else if (!_targetEscadreEntityIds.Any() && !CurrentDestination.HasValue) {
                        stillActivelyCommandedToMove = false; // No more attack targets, no destination
                    }
                }
            }
            else if (CurrentDestination.HasValue)
            {
                _fleetCommandTargetPoint = new Vector3(CurrentDestination.Value.X, this.Position.Y, CurrentDestination.Value.Y);
                // Check if current average position has reached the destination
                if ((new Vector2(this.Position.X, this.Position.Z) - CurrentDestination.Value).SqrMagnitude < 1.0f * 1.0f) // Reduced threshold
                {
                    CurrentDestination = null; // Reached destination
                    stillActivelyCommandedToMove = false; // No longer moving to this destination
                    Logger.Log($"[Escadre {Id}] Reached destination ({_fleetCommandTargetPoint}). Setting to Hold.");
                }
                else {
                    stillActivelyCommandedToMove = true;
                }
            }
            else // No attack targets, no current destination
            {
                stillActivelyCommandedToMove = false;
            }

            // Update _isFleetMovingToTarget and handle transition to holding (fix _fleetCommandTargetPoint)
            if (stillActivelyCommandedToMove)
            {
                _isFleetMovingToTarget = true;
            }
            else // Not actively commanded to move/attack
            {
                if (_isFleetMovingToTarget) // Was moving, now transitioning to hold
                {
                    _fleetCommandTargetPoint = this.Position; // Set the hold anchor point to current average
                    Logger.Log($"[Escadre {Id}] Transitioning to Hold state. FleetCommandTargetPoint fixed at current avg: {_fleetCommandTargetPoint}.");
                }
                _isFleetMovingToTarget = false;
                // _fleetCommandTargetPoint now remains fixed at the point where it stopped/started holding.
            }
             _wasPreviouslyMovingToTarget = _isFleetMovingToTarget; // Store for next frame's transition detection

            // --- Update Escadre.Position (Average of ships) ---
            Vector3 newAveragePosition = CalculateAverageShipPosition();
            if (this.Position != newAveragePosition)
            {
                this.Position = newAveragePosition;
            }
            
            // --- Update Escadre.Rotation (Fleet Orientation) ---
            if (_isFleetMovingToTarget && (_fleetCommandTargetPoint - this.Position).SqrMagnitude > 0.1f * 0.1f)
            {
                Vector3 directionToCommandTarget = (_fleetCommandTargetPoint - this.Position).Normalized;
                if (directionToCommandTarget.SqrMagnitude > Vector3.Epsilon)
                {
                    this.Rotation = Quaternion.LookRotation(directionToCommandTarget, Vector3.Up);
                }
            }
            // If not moving, rotation remains as it was.

            // --- Update CurrentFleetSpeed ---
            if (_isFleetMovingToTarget)
            {
                // Formation anchor for deviation calculation is the point ships are trying to form around
                Vector3 anchorForDeviationCalc = _fleetCommandTargetPoint; 
                float averageDeviation = CalculateAverageShipDeviation(anchorForDeviationCalc, this.Rotation);
                float normalizedDeviation = Math.Clamp(averageDeviation / MaxFormationSpreadRadius, 0f, 1f);
                float targetSpeedFactor = 1.0f - (normalizedDeviation * FormationIntegrityFactor);
                float desiredSpeed = FleetMaxSpeed * targetSpeedFactor;
                
                CurrentFleetSpeed = MathUtils.MoveTowards(CurrentFleetSpeed, desiredSpeed, FleetAcceleration * deltaTime);
            }
            else 
            {
                CurrentFleetSpeed = MathUtils.MoveTowards(CurrentFleetSpeed, 0f, FleetAcceleration * 2f * deltaTime); 
            }

            // --- Update Individual Ship Movement Targets ---
            // This uses the now-stable _fleetCommandTargetPoint when holding.
            UpdateShipMovementTargets(_level.CurrentTime);
        }

        private float CalculateAverageShipDeviation(Vector3 formationAnchor, Quaternion fleetOrientation)
        {
            if (!_shipEntityIds.Any()) return 0f;

            float totalDeviation = 0f;
            int aliveShipCount = 0;

            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    Vector2 relativeOffset2D = CurrentFormation.GetShipRelativeOffset(shipId);
                    Vector3 relativeOffset3D = new Vector3(relativeOffset2D.X, 0f, relativeOffset2D.Y);
                    Vector3 idealSlotWorldPosition = formationAnchor + (fleetOrientation * relativeOffset3D);
                    
                    totalDeviation += (ship.Position - idealSlotWorldPosition).Magnitude;
                    aliveShipCount++;
                }
            }
            return aliveShipCount > 0 ? totalDeviation / aliveShipCount : 0f;
        }


        private Vector3 CalculateAverageShipPosition()
        {
            if (!_shipEntityIds.Any())
            {
                return this.Position; 
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
            return _isFleetMovingToTarget ? _fleetCommandTargetPoint : this.Position; 
        }


        internal void UpdateShipMovementTargets(float serverTime)
        {
            if (_isDisbanding || IsDead) return;

            // _fleetCommandTargetPoint is now correctly managed for both moving and holding states by Escadre.Update()
            Vector3 currentFormationAnchorPoint = _fleetCommandTargetPoint; 
            Quaternion fleetOrientation = this.Rotation; 

            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    Vector2 relativeOffset2D = CurrentFormation.GetShipRelativeOffset(shipId);
                    Vector3 relativeOffset3D = new Vector3(relativeOffset2D.X, 0f, relativeOffset2D.Y);
                    Vector3 worldOffsetFromAnchor = fleetOrientation * relativeOffset3D;
                    Vector3 targetShipWorldPosition = currentFormationAnchorPoint + worldOffsetFromAnchor;
                    
                    ship.SetMovementTarget(new Vector2(targetShipWorldPosition.X, targetShipWorldPosition.Z), serverTime);
                }
            }
        }
        
        public Vector3 CalculateGeometricCenterOfShips() 
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
            CurrentFleetSpeed = 0f;

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
            CurrentFleetSpeedChanged = null; 
            if (CurrentFormation != null)
            {
                CurrentFormation.OnFormationLayoutChanged -= HandleInternalFormationLayoutChange;
                CurrentFormation.RemoveAllShips(); 
            }
        }
    }
}