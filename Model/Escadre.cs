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

        public Vector2? CurrentDestination { get; private set; } 
        private readonly HashSet<int> _targetEscadreEntityIds = new HashSet<int>();
        public IReadOnlyCollection<int> TargetEscadreEntityIds => _targetEscadreEntityIds;
        
        // --- New/Renamed Movement Internals ---
        /// <summary>
        /// The ultimate world position the fleet is trying to reach (either CurrentDestination or an enemy Escadre's position).
        /// </summary>
        private Vector3 _activeCommandTargetWorld; 
        /// <summary>
        /// The current world position of the formation's center point. This point moves towards _activeCommandTargetWorld.
        /// Ships form up relative to this anchor.
        /// </summary>
        private Vector3 _currentFormationAnchorWorld;
        /// <summary>
        /// True if _currentFormationAnchorWorld is actively moving towards _activeCommandTargetWorld.
        /// </summary>
        private bool _isAnchorMovingToCommandTarget;
        private bool _wasAnchorPreviouslyMoving = false; // To detect transitions for anchor initialization

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
        public float FormationScale { get; private set; }

        public Escadre(Level level, int ownerClientId, string nickname, Vector3 initialPosition) : base(level)
        {
            OwnerClientId = ownerClientId;
            Nickname = nickname ?? $"Escadre_{OwnerClientId}";
            
            this.Position = initialPosition; // Entity.Position will be average of ships
            this.Rotation = Quaternion.Identity; 
            
            _currentFormationAnchorWorld = initialPosition; // Formation anchor starts at initial position
            _activeCommandTargetWorld = initialPosition;  // Initially, command target is to hold here
            _isAnchorMovingToCommandTarget = false;
            _wasAnchorPreviouslyMoving = false;

            _resources = 1000; 
            CurrentFormation = new Formation(OwnerClientId, new DefaultFormationValidationStrategy());
            CurrentFormation.OnFormationLayoutChanged += HandleInternalFormationLayoutChange;

            FleetMaxSpeed = 3.5f; 
            _currentFleetSpeed = 0f; 
            FormationIntegrityFactor = 1.0f;
            FleetAcceleration = 1.0f; 
            MaxFormationSpreadRadius = 35f; 
            FormationScale = 1.0f;
        }

        private void HandleInternalFormationLayoutChange()
        {
            OnFormationChanged?.Invoke(CurrentFormation);
            if (!_isDisbanding) UpdateShipMovementTargets(_level.CurrentTime);
        }

        public void SetFormationScale(float newScale, float serverTime)
        {
            if (newScale <= 0)
            {
                Logger.LogWarning($"[Escadre {Id}] Invalid formation scale: {newScale}. Must be positive.");
                return;
            }
            if (Math.Abs(FormationScale - newScale) > Core.Primitives.Vector3.Epsilon) 
            {
                FormationScale = newScale;
                Logger.Log($"[Escadre {Id}] Formation scale set to {FormationScale}.");
                UpdateShipMovementTargets(serverTime); 
            }
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
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Added Ship {ship.Id} with initial requested/auto offset {finalOffset}. Total ships: {_shipEntityIds.Count}. Formation will re-center.");
                if (_shipEntityIds.Count == 1 && !_isAnchorMovingToCommandTarget) 
                {
                    // If first ship and not moving, anchor is at current average (which will be this ship's pos)
                    _currentFormationAnchorWorld = CalculateAverageShipPosition(); 
                    _activeCommandTargetWorld = _currentFormationAnchorWorld; // Hold at current position
                }
                 // HandleInternalFormationLayoutChange -> UpdateShipMovementTargets will be called
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
                    _isAnchorMovingToCommandTarget = false; 
                    CurrentFleetSpeed = 0f;
                }
                 // HandleInternalFormationLayoutChange -> UpdateShipMovementTargets will be called
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
            
            bool wasAlreadyMovingToPoint = CurrentDestination.HasValue && _isAnchorMovingToCommandTarget;

            CurrentDestination = destination;
            _targetEscadreEntityIds.Clear(); // Explicit move order cancels attack orders
            _isAnchorMovingToCommandTarget = true; 

            // If this is a new move command (wasn't moving, or was attacking)
            // or if the destination changed significantly.
            // For simplicity, always re-anchor on SetCourse if it wasn't already moving to a point.
            if (!wasAlreadyMovingToPoint)
            {
                 _currentFormationAnchorWorld = this.Position; // Start moving anchor from current fleet avg position
            }
            // _activeCommandTargetWorld will be updated in Update() based on new CurrentDestination
            
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Setting course to {destination}. Anchor moving: {_isAnchorMovingToCommandTarget}.");
            UpdateShipMovementTargets(serverTime); // Ensure ships get their new targets relative to the (potentially new) anchor
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
                bool newTargetAdded = _targetEscadreEntityIds.Add(targetEscadreEntityId);
                if (newTargetAdded) {
                     Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Added attack order on escadre entity {targetEscadreEntityId} (Owner {targetEscadre.OwnerClientId}).");
                }
                
                bool wasAlreadyAttacking = _targetEscadreEntityIds.Count > 1 || (newTargetAdded && _targetEscadreEntityIds.Count == 1 && CurrentDestination.HasValue);

                CurrentDestination = null; // Attack order cancels fixed move orders
                _isAnchorMovingToCommandTarget = true; 

                // If this is a new attack command (wasn't attacking, or was moving to point)
                if (!wasAlreadyAttacking || !_targetEscadreEntityIds.Contains(targetEscadreEntityId)) // Or if primary target changed
                {
                    _currentFormationAnchorWorld = this.Position; // Start moving anchor from current fleet avg position
                }
                // _activeCommandTargetWorld will be updated in Update()
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Attack order on entity {targetEscadreEntityId} initiated. Anchor moving: {_isAnchorMovingToCommandTarget}.");
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
                if (!CurrentDestination.HasValue) // If no move order to fall back to, stop moving anchor
                {
                    _isAnchorMovingToCommandTarget = false; 
                    // _currentFormationAnchorWorld will be set to current position in Update() when transitioning to hold
                }
                // else, if CurrentDestination exists, Update() will handle moving towards it.
                UpdateShipMovementTargets(_level.CurrentTime); 
            } else {
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] No active attack orders to cancel.");
            }
        }
        
        public bool RequestSetFormation(List<Tuple<int, Vector2>> newFormationSlots, float serverTime)
        {
            if (_isDisbanding || IsDead) return false;
            var proposedSlotsData = new List<Tuple<int, Vector2>>(newFormationSlots); 

            HashSet<int> requestShipIds = new HashSet<int>(proposedSlotsData.Select(s => s.Item1));
            HashSet<int> escadreShipIds = new HashSet<int>(_shipEntityIds);

            if (!requestShipIds.SetEquals(escadreShipIds))
            {
                Logger.LogWarning($"[Escadre {Id}] RequestSetFormation: Ship ID mismatch. Request: [{string.Join(",", requestShipIds)}], Escadre: [{string.Join(",", escadreShipIds)}]. Request rejected.");
                return false;
            }
            
            var proposedFormationForValidation = proposedSlotsData.Select(s => new FormationSlot(s.Item2, s.Item1)).ToList();
            if (!CurrentFormation.ValidationStrategy.IsValidFormation(proposedFormationForValidation, out string reason))
            {
                Logger.LogWarning($"[Escadre {Id}] Proposed formation (raw offsets) is invalid: {reason}");
                return false;
            }

            bool changedOverall = false;
            foreach (var slotData in proposedSlotsData)
            {
                if (CurrentFormation.TrySetShipRelativeOffset(slotData.Item1, slotData.Item2, out _))
                {
                    changedOverall = true; 
                }
            }
            
            if (changedOverall)
            {
                Logger.Log($"[Escadre {Id}] Formation updated successfully via request. Final layout will be centered.");
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
                _isAnchorMovingToCommandTarget = false;
                return;
            }
            
            // 1. Determine _activeCommandTargetWorld (the ultimate goal)
            bool hasActiveCommand = false;
            if (TargetEscadreEntityIds.Any())
            {
                int primaryTargetId = TargetEscadreEntityIds.First(); 
                if (_level.TryGetEntity(primaryTargetId, out Entity targetEntity) && targetEntity is Escadre enemyEscadre && !enemyEscadre.IsDead)
                {
                    _activeCommandTargetWorld = enemyEscadre.Position; 
                    hasActiveCommand = true;
                }
                else // Primary attack target lost
                {
                    _targetEscadreEntityIds.Remove(primaryTargetId); // Remove invalid target
                    if (TargetEscadreEntityIds.Any()) // Still other attack targets?
                    {
                        // _activeCommandTargetWorld will be updated in next loop for new primary
                        hasActiveCommand = true; 
                    }
                    else if (CurrentDestination.HasValue) // Fallback to move order
                    {
                        _activeCommandTargetWorld = new Vector3(CurrentDestination.Value.X, _currentFormationAnchorWorld.Y, CurrentDestination.Value.Y);
                        hasActiveCommand = true;
                    }
                    else // No more attack targets, no destination
                    {
                        hasActiveCommand = false;
                    }
                }
            }
            else if (CurrentDestination.HasValue)
            {
                _activeCommandTargetWorld = new Vector3(CurrentDestination.Value.X, _currentFormationAnchorWorld.Y, CurrentDestination.Value.Y);
                hasActiveCommand = true;
            }
            else // No attack targets, no current destination
            {
                hasActiveCommand = false;
            }

            // 2. Update _isAnchorMovingToCommandTarget and _currentFormationAnchorWorld
            if (hasActiveCommand && _isAnchorMovingToCommandTarget)
            {
                Vector3 directionToActiveCommandTarget = (_activeCommandTargetWorld - _currentFormationAnchorWorld);
                float distanceToActiveCommandTargetSq = directionToActiveCommandTarget.SqrMagnitude;

                // Close enough threshold (e.g., 1.0f for fixed points, maybe larger for chasing moving targets)
                float arrivalThresholdSq = (CurrentDestination.HasValue ? 1.0f * 1.0f : FleetMaxSpeed * 0.5f * FleetMaxSpeed * 0.5f ); // Smaller for fixed points

                if (distanceToActiveCommandTargetSq < arrivalThresholdSq)
                {
                    if (CurrentDestination.HasValue) // Reached a fixed destination
                    {
                        _currentFormationAnchorWorld = _activeCommandTargetWorld; // Snap to target
                        CurrentDestination = null; // Clear fixed destination
                        _isAnchorMovingToCommandTarget = false; // Stop anchor movement
                        Logger.Log($"[Escadre {Id}] Reached CommandTarget ({_activeCommandTargetWorld}). Anchor stopped.");
                    }
                    // If it was an attack target, keep moving/updating anchor as target moves, unless other logic stops it.
                    // The logic here means the anchor will "stick" to a very close attack target.
                }
                
                // If still set to move (might have been unset above if destination reached)
                if(_isAnchorMovingToCommandTarget) 
                {
                    if (directionToActiveCommandTarget.SqrMagnitude > Vector3.Epsilon)
                    {
                        this.Rotation = Quaternion.LookRotation(directionToActiveCommandTarget.Normalized, Vector3.Up);
                    }
                    _currentFormationAnchorWorld += this.Rotation * Vector3.Forward * CurrentFleetSpeed * deltaTime;
                }
            }
            else // Not actively commanded to move or _isAnchorMovingToCommandTarget is false (holding)
            {
                if (_isAnchorMovingToCommandTarget) // Was moving, now transitioning to hold (e.g. command cancelled, no new command)
                {
                    // Fix the anchor at the current fleet's average position when hold starts
                    _currentFormationAnchorWorld = CalculateAverageShipPosition(); // Or this.Position before it's updated
                    _activeCommandTargetWorld = _currentFormationAnchorWorld; // No further target
                    Logger.Log($"[Escadre {Id}] Transitioning to Hold state. Formation Anchor fixed at current fleet avg: {_currentFormationAnchorWorld}.");
                }
                _isAnchorMovingToCommandTarget = false;
                // _currentFormationAnchorWorld remains fixed at the point where it stopped/started holding.
            }

            // 3. Update Entity.Position (average of ships)
            Vector3 newAveragePosition = CalculateAverageShipPosition();
            if (this.Position != newAveragePosition)
            {
                this.Position = newAveragePosition;
            }
            
            // 4. Update CurrentFleetSpeed
            if (_isAnchorMovingToCommandTarget && hasActiveCommand) // Accelerate/Maintain speed if moving towards a target
            {
                float averageDeviation = CalculateAverageShipDeviation(_currentFormationAnchorWorld, this.Rotation);
                float normalizedDeviation = Math.Clamp(averageDeviation / MaxFormationSpreadRadius, 0f, 1f);
                float targetSpeedFactor = 1.0f - (normalizedDeviation * FormationIntegrityFactor);
                float desiredSpeed = FleetMaxSpeed * targetSpeedFactor;
                CurrentFleetSpeed = MathUtils.MoveTowards(CurrentFleetSpeed, desiredSpeed, FleetAcceleration * deltaTime);
            }
            else // Decelerate if holding or no command
            {
                CurrentFleetSpeed = MathUtils.MoveTowards(CurrentFleetSpeed, 0f, FleetAcceleration * 2f * deltaTime); 
            }

            // 5. Update Individual Ship Movement Targets (relative to the now updated _currentFormationAnchorWorld)
            UpdateShipMovementTargets(_level.CurrentTime);
            _wasAnchorPreviouslyMoving = _isAnchorMovingToCommandTarget;
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
                    Vector2 relativeCenteredOffset2D = CurrentFormation.GetShipRelativeOffset(shipId);
                    Vector3 relativeScaledOffset3D = new Vector3(relativeCenteredOffset2D.X * FormationScale, 0f, relativeCenteredOffset2D.Y * FormationScale);
                    Vector3 idealSlotWorldPosition = formationAnchor + (fleetOrientation * relativeScaledOffset3D);
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
                // If no ships, its "position" could be its formation anchor, or last known if that makes sense.
                return _currentFormationAnchorWorld; 
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
            // Fallback if all ships died this frame before avg could be calculated, or if list empty.
            return _currentFormationAnchorWorld; 
        }

        internal void UpdateShipMovementTargets(float serverTime)
        {
            if (_isDisbanding || IsDead) return;

            // Ships always form around _currentFormationAnchorWorld, using Escadre.Rotation for orientation
            Quaternion fleetOrientation = this.Rotation; 

            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    Vector2 relativeCenteredOffset2D = CurrentFormation.GetShipRelativeOffset(shipId);
                    Vector3 relativeScaledOffset3D = new Vector3(relativeCenteredOffset2D.X * FormationScale, 0f, relativeCenteredOffset2D.Y * FormationScale);
                    Vector3 worldOffsetFromAnchor = fleetOrientation * relativeScaledOffset3D;
                    Vector3 targetShipWorldPosition = _currentFormationAnchorWorld + worldOffsetFromAnchor;
                    
                    ship.SetMovementTarget(new Vector2(targetShipWorldPosition.X, targetShipWorldPosition.Z), serverTime);
                }
            }
        }
        
        public Vector3 CalculateGeometricCenterOfShips() 
        {
            return CalculateAverageShipPosition();
        }

        // Exposed for debug purposes
        public Vector3 GetFormationAnchorWorldPosition() => _currentFormationAnchorWorld;
        public Vector3 GetActiveCommandTargetWorldPosition() => _activeCommandTargetWorld;
        public bool GetIsAnchorMovingToCommandTarget() => _isAnchorMovingToCommandTarget;


        internal void Disband(bool silentKillShips = true)
        {
            _isDisbanding = true; 
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Disbanding (silentKillShips: {silentKillShips}).");
            
            CurrentDestination = null;
            _targetEscadreEntityIds.Clear();
            _isAnchorMovingToCommandTarget = false;
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