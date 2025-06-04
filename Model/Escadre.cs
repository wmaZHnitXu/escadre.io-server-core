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
        public Vector2? CurrentDestination
        {
            get => _currentDestination;
            private set
            {
                bool changed = (_currentDestination.HasValue != value.HasValue) ||
                               (_currentDestination.HasValue && value.HasValue && _currentDestination.Value != value.Value);
                if (changed)
                {
                    _currentDestination = value;
                    CurrentDestinationChangedEvent?.Invoke(_currentDestination);
                }
            }
        }

        private readonly HashSet<int> _targetEscadreEntityIds = new HashSet<int>();
        public IReadOnlyCollection<int> TargetEscadreEntityIds => _targetEscadreEntityIds;


        private Vector3 _activeCommandTargetWorld;
        private Vector3 _currentFormationAnchorWorld;
        private bool _isAnchorMovingToCommandTarget;
        private bool _wasAnchorPreviouslyMoving = false; // Used to detect state changes for logging/logic

        private int _resources;
        public int Resources { get => _resources; private set => _resources = value; }

        public event Action<int> OnResourcesChanged;
        public event Action<Formation> OnFormationChanged;
        public event Action<float> CurrentFleetSpeedChanged;
        public event Action<Vector2?> CurrentDestinationChangedEvent;
        public event Action<IReadOnlyCollection<int>> TargetEscadreEntityIdsChangedEvent;


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
                if (Math.Abs(previousValue - _currentFleetSpeed) > 0.01f || (previousValue == 0 && _currentFleetSpeed != 0) || (previousValue != 0 && _currentFleetSpeed == 0))
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

            this.Position = initialPosition; // Escadre's own entity position, updated to fleet center
            this.Rotation = Quaternion.Identity;

            _currentFormationAnchorWorld = initialPosition; // Initial anchor position
            _activeCommandTargetWorld = initialPosition;    // Initial command target
            _isAnchorMovingToCommandTarget = false;
            _wasAnchorPreviouslyMoving = false;

            _resources = 1000; // Example starting resources
            CurrentFormation = new Formation(OwnerClientId, new DefaultFormationValidationStrategy());
            CurrentFormation.OnFormationLayoutChanged += HandleInternalFormationLayoutChange;

            FleetMaxSpeed = 7.5f; // Example
            _currentFleetSpeed = 0f;
            FormationIntegrityFactor = 0.9f;
            FleetAcceleration = 10.0f; // Example
            MaxFormationSpreadRadius = 10f; // Example
            FormationScale = 2.0f; // Example
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
            if (amount <= 0) return true; // Deducting zero or negative is always "successful"
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
            // Example fixed cost, could be dynamic
            return 50;
        }

        internal void AddShip(Ship ship, Vector2? initialFormationOffset = null)
        {
            if (_isDisbanding) return;
            if (ship == null || ship.OwningEscadreClientId != OwnerClientId)
            {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Attempted to add invalid ship. Ship ID: {ship?.Id}");
                return;
            }
            if (!_shipEntityIds.Contains(ship.Id))
            {
                _shipEntityIds.Add(ship.Id);
                bool addedToFormation;
                Vector2 finalOffset; // Not strictly used after this, but good for debugging
                if (initialFormationOffset.HasValue)
                {
                    addedToFormation = CurrentFormation.TryAddShip(ship.Id, initialFormationOffset.Value, out string reason);
                    finalOffset = initialFormationOffset.Value;
                    if (!addedToFormation) Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Failed to add ship {ship.Id} to formation at preferred offset {initialFormationOffset.Value}: {reason}");
                }
                else
                {
                    addedToFormation = CurrentFormation.AssignShipToAutoSlot(ship.Id, out finalOffset);
                    if (!addedToFormation) Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Failed to auto-assign ship {ship.Id} to formation.");
                }
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Added Ship {ship.Id}. Total ships: {_shipEntityIds.Count}. Added to formation: {addedToFormation}");
                // If this is the first ship and escadre isn't already moving, set anchor to ship's position.
                if (_shipEntityIds.Count == 1 && !_isAnchorMovingToCommandTarget)
                {
                    _currentFormationAnchorWorld = ship.Position; // Start anchor at the first ship's pos
                    _activeCommandTargetWorld = _currentFormationAnchorWorld; // Command target is where it is
                }
                // UpdateShipMovementTargets will be called by HandleInternalFormationLayoutChange if formation changed
            }
        }

        private void RemoveShip(int shipId)
        {
            if (_isDisbanding) return;
            if (_shipEntityIds.Remove(shipId))
            {
                CurrentFormation.RemoveShip(shipId); // This will trigger OnFormationLayoutChanged -> UpdateShipMovementTargets
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Removed Ship {shipId}. Total ships: {_shipEntityIds.Count}");
                if (!_shipEntityIds.Any())
                {
                    Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] All ships lost! Escadre may stop or be destroyed.");
                    _isAnchorMovingToCommandTarget = false; // Stop if it was moving
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
                this.Kill(); // This will eventually call Death() -> Disband()
            }
        }

        public void SetCourse(Vector2 destination, float serverTime)
        {
            if (_isDisbanding || IsDead || !_shipEntityIds.Any()) return;

            CurrentDestination = destination; // This fires event if changed

            // _isAnchorMovingToCommandTarget will be set true in Update() if CurrentDestination is valid.
            // The anchor's starting position for new movement is handled in Update().
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Setting course to {destination}.");
            // UpdateShipMovementTargets is called at the end of Escadre.Update()
        }

        public void OrderAttackEscadreEntity(int targetEscadreEntityId, float serverTime)
        {
            if (_isDisbanding || IsDead || !_shipEntityIds.Any()) return;
            if (targetEscadreEntityId == this.Id)
            {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Cannot target self for attack.");
                return;
            }

            if (_level.TryGetEntity(targetEscadreEntityId, out Entity targetEntity) && targetEntity is Escadre targetEscadre && !targetEscadre.IsDead)
            {
                if (_targetEscadreEntityIds.Add(targetEscadreEntityId))
                {
                    Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Added attack order on escadre entity {targetEscadreEntityId}.");
                    TargetEscadreEntityIdsChangedEvent?.Invoke(new List<int>(_targetEscadreEntityIds));
                }
            }
            else
            {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] OrderAttackEscadreEntity: Target Escadre Entity ID {targetEscadreEntityId} not found, not an Escadre, or is dead.");
            }
        }

        public void OrderCancelAttack()
        {
            if (_isDisbanding || IsDead) return;
            if (TargetEscadreEntityIds.Any())
            {
                _targetEscadreEntityIds.Clear();
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Cancelling ALL attack orders.");
                TargetEscadreEntityIdsChangedEvent?.Invoke(new List<int>(_targetEscadreEntityIds));
            }
            else
            {
                // Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] No active attack orders to cancel.");
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
                if (CurrentFormation.TrySetShipRelativeOffset(slotData.Item1, slotData.Item2, out _)) // Reason from TrySetShipRelativeOffset ignored here
                {
                    changedOverall = true;
                }
            }

            if (changedOverall)
            {
                Logger.Log($"[Escadre {Id}] Formation updated successfully via request.");
                // HandleInternalFormationLayoutChange -> UpdateShipMovementTargets will be called.
                return true;
            }
            return false;
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            if (IsDead || _isDisbanding) // No ships check is handled below for speed/movement
            {
                CurrentFleetSpeed = 0f;
                _isAnchorMovingToCommandTarget = false;
                return;
            }
            if (!_shipEntityIds.Any()) // If no ships, it cannot move or maintain formation speed
            {
                CurrentFleetSpeed = 0f;
                _isAnchorMovingToCommandTarget = false; // No ships to move the anchor
                                                        // Escadre entity position might just stay at _currentFormationAnchorWorld or last known average.
                this.Position = _currentFormationAnchorWorld;
                return;
            }


            bool previousAnchorMovingState = _isAnchorMovingToCommandTarget;

            // Determine if the anchor should be moving based on CurrentDestination
            if (CurrentDestination.HasValue)
            {
                _activeCommandTargetWorld = new Vector3(CurrentDestination.Value.X, _currentFormationAnchorWorld.Y, CurrentDestination.Value.Y);
                _isAnchorMovingToCommandTarget = true; // Anchor intends to move

                Vector3 directionToWaypoint = _activeCommandTargetWorld - _currentFormationAnchorWorld;
                if (directionToWaypoint.SqrMagnitude < (0.25f * 0.25f)) // Arrival threshold (e.g., 0.25 units)
                {
                    // ARRIVED AT DESTINATION
                    _currentFormationAnchorWorld = _activeCommandTargetWorld; // Anchor IS the destination
                    CurrentDestination = null; // Clear waypoint, fires event, signals arrival
                    _isAnchorMovingToCommandTarget = false; // Anchor STOPS active movement towards command
                    Logger.Log($"[Escadre {Id}] Reached waypoint. Anchor fixed at {_currentFormationAnchorWorld}.");
                }
            }
            else // No CurrentDestination
            {
                _isAnchorMovingToCommandTarget = false; // Not moving to a command target
            }

            // Periodically check and clean up invalid TargetEscadreEntityIds.
            if (TargetEscadreEntityIds.Any())
            {
                List<int> targetsToRemove = null;
                foreach (int targetId in TargetEscadreEntityIds)
                {
                    if (!_level.TryGetEntity(targetId, out Entity targetEntity) || !(targetEntity is Escadre) || targetEntity.IsDead)
                    {
                        if (targetsToRemove == null) targetsToRemove = new List<int>();
                        targetsToRemove.Add(targetId);
                    }
                }
                if (targetsToRemove != null && targetsToRemove.Any())
                {
                    foreach (int idToRemove in targetsToRemove) _targetEscadreEntityIds.Remove(idToRemove);
                    TargetEscadreEntityIdsChangedEvent?.Invoke(new List<int>(_targetEscadreEntityIds));
                    Logger.Log($"[Escadre {Id}] Removed {targetsToRemove.Count} invalid target escadres. Remaining: {TargetEscadreEntityIds.Count}");
                }
            }

            // --- Anchor Position and Rotation Logic ---
            if (_isAnchorMovingToCommandTarget) // If moving towards a CurrentDestination
            {
                if (!previousAnchorMovingState && _shipEntityIds.Any()) // Just started moving towards a NEW destination
                {
                    // When a "SetCourse" is given and escadre wasn't moving, start anchor from current fleet center.
                    _currentFormationAnchorWorld = CalculateAverageShipPosition();
                    Logger.Log($"[Escadre {Id}] Anchor initiating movement from current fleet pos {_currentFormationAnchorWorld} towards command target {_activeCommandTargetWorld}.");
                }
                // Else (continuing towards an existing destination), _currentFormationAnchorWorld is already being updated.

                Vector3 directionToCommandTarget = (_activeCommandTargetWorld - _currentFormationAnchorWorld).NormalizedSafe(this.Rotation * Vector3.Forward);
                this.Rotation = Quaternion.LookRotation(directionToCommandTarget, Vector3.Up);
                _currentFormationAnchorWorld += directionToCommandTarget * CurrentFleetSpeed * deltaTime;
            }
            else // Not actively moving towards a command target (i.e., holding position or just arrived)
            {
                if (previousAnchorMovingState) // Was moving, NOW STOPPED (because CurrentDestination became null OR was reached)
                {
                    // _currentFormationAnchorWorld is ALREADY set to _activeCommandTargetWorld if it was reached.
                    // No need to re-calculate to average ship position here; the anchor IS the target it just reached.
                    _activeCommandTargetWorld = _currentFormationAnchorWorld; // Ensure command target aligns with fixed anchor
                    Logger.Log($"[Escadre {Id}] Anchor movement stopped. Anchor fixed at: {_currentFormationAnchorWorld}.");
                }
                // else: Was already stopped, and is still stopped. Anchor remains where it is.
                // When holding, escadre's own rotation reflects the average orientation of its ships.
                if (_shipEntityIds.Any()) this.Rotation = CalculateAverageShipRotation();
            }

            // Update Escadre's own Entity.Position to reflect the fleet's center of mass (for PVS etc.)
            if (_shipEntityIds.Any()) this.Position = CalculateAverageShipPosition();
            else this.Position = _currentFormationAnchorWorld; // If no ships, escadre entity is at its anchor


            // --- Fleet Speed Calculation ---
            if (_isAnchorMovingToCommandTarget)
            {
                float averageDeviation = CalculateAverageShipDeviation(_currentFormationAnchorWorld, this.Rotation);
                float normalizedDeviation = Math.Clamp(averageDeviation / MaxFormationSpreadRadius, 0f, 1f);
                float targetSpeedFactor = 1.0f - (normalizedDeviation * FormationIntegrityFactor);
                float desiredSpeed = FleetMaxSpeed * targetSpeedFactor;
                CurrentFleetSpeed = MathUtils.MoveTowards(CurrentFleetSpeed, desiredSpeed, FleetAcceleration * deltaTime);
            }
            else // Not moving to a command target (holding or just arrived), so slow down to zero.
            {
                CurrentFleetSpeed = MathUtils.MoveTowards(CurrentFleetSpeed, 0f, FleetAcceleration * 2f * deltaTime); // Faster deceleration when stopping
            }

            UpdateShipMovementTargets(_level.CurrentTime);
            _wasAnchorPreviouslyMoving = _isAnchorMovingToCommandTarget; // Store for next frame's logic
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
                return _currentFormationAnchorWorld; // If no ships, return anchor's last known position
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
            // Fallback if all ships are somehow dead but IDs still present (should be rare)
            // or if _shipEntityIds is populated but none are found/alive in level.
            return _currentFormationAnchorWorld;
        }

        private Quaternion CalculateAverageShipRotation()
        {
            if (!_shipEntityIds.Any())
            {
                return this.Rotation; // Maintain current escadre rotation if no ships
            }

            Quaternion averageRotation = Quaternion.Identity;
            int aliveShipCount = 0;
            bool firstShip = true;

            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    if (firstShip)
                    {
                        averageRotation = ship.Rotation;
                        firstShip = false;
                    }
                    else
                    {
                        // Iterative Slerp for averaging. For many ships, more robust methods exist.
                        // Weight for new ship is 1 / (count_so_far + 1)
                        averageRotation = Quaternion.Slerp(averageRotation, ship.Rotation, 1.0f / (aliveShipCount + 1.0f));
                    }
                    aliveShipCount++;
                }
            }
            return aliveShipCount > 0 ? averageRotation.Normalized : this.Rotation; // Fallback if all ships dead
        }


        internal void UpdateShipMovementTargets(float serverTime)
        {
            if (_isDisbanding || IsDead) return;

            Quaternion fleetOrientationForFormation = this.Rotation; // Use escadre's current (potentially averaged) orientation

            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    Vector2 relativeCenteredOffset2D = CurrentFormation.GetShipRelativeOffset(shipId);
                    Vector3 relativeScaledOffset3D = new Vector3(relativeCenteredOffset2D.X * FormationScale, 0f, relativeCenteredOffset2D.Y * FormationScale);

                    // _currentFormationAnchorWorld is now correctly set:
                    // - If moving: it's the advancing anchor.
                    // - If holding/just arrived: it's the fixed commanded destination.
                    Vector3 targetShipWorldPosition = _currentFormationAnchorWorld + (fleetOrientationForFormation * relativeScaledOffset3D);

                    ship.SetMovementTarget(new Vector2(targetShipWorldPosition.X, targetShipWorldPosition.Z), serverTime);
                }
            }
        }

        public Vector3 CalculateGeometricCenterOfShips()
        {
            return CalculateAverageShipPosition();
        }

        public Vector3 GetFormationAnchorWorldPosition() => _currentFormationAnchorWorld;
        public Vector3 GetActiveCommandTargetWorldPosition() => _activeCommandTargetWorld;
        public bool GetIsAnchorMovingToCommandTarget() => _isAnchorMovingToCommandTarget;


        internal void Disband(bool silentKillShips = true)
        {
            _isDisbanding = true; // Prevent re-entrant calls or updates during disband
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Disbanding (silentKillShips: {silentKillShips}).");

            CurrentDestination = null; // Stop any movement commands
            _targetEscadreEntityIds.Clear();
            TargetEscadreEntityIdsChangedEvent?.Invoke(new List<int>(_targetEscadreEntityIds)); // Notify listeners

            _isAnchorMovingToCommandTarget = false;
            CurrentFleetSpeed = 0f;

            // Create a copy for iteration as Kill might modify collections indirectly
            var idsToKill = new List<int>(_shipEntityIds);
            foreach (int shipIdInList in idsToKill)
            {
                if (_level.TryGetEntity(shipIdInList, out Entity entityInLevel) && !entityInLevel.IsDead)
                {
                    // If silentKillShips is true, ships won't trigger their OnDestructionEvent for clients
                    // but their OnDeathEvent will still fire for server-side cleanup (e.g., Level removing them).
                    entityInLevel.Kill(silentKillShips);
                }
            }
            _shipEntityIds.Clear(); // Clear the list of ships in this escadre
            CurrentFormation.RemoveAllShips(); // Clear formation slots
            _isDisbanding = false; // Disbanding process complete
        }

        protected override void Death()
        {
            // This is called when Escadre.Kill(false) is invoked (non-silent death)
            base.Death(); // Base Entity.Death() does nothing by default.
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Performing Death(). Disbanding ships (non-silently).");
            Disband(false); // Ships die non-silently, triggering their own OnDestructionEvent for clients.
        }

        protected override void ObligatoryOnRemove()
        {
            // This is called when the Escadre is finally removed from the Level,
            // regardless of whether Kill was silent or not.
            base.ObligatoryOnRemove();
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Performing ObligatoryOnRemove().");
            if (!_isDisbanding && !IsDead) // If removed while not dead and not already disbanding (e.g. admin command, level reset)
            {
                // Ensure ships are cleaned up if escadre is removed for other reasons.
                Disband(true); // Silently kill ships as the escadre is just vanishing.
            }

            // Nullify events to prevent memory leaks and dangling references
            OnResourcesChanged = null;
            OnFormationChanged = null;
            CurrentFleetSpeedChanged = null;
            CurrentDestinationChangedEvent = null;
            TargetEscadreEntityIdsChangedEvent = null;

            if (CurrentFormation != null)
            {
                CurrentFormation.OnFormationLayoutChanged -= HandleInternalFormationLayoutChange;
                // CurrentFormation.RemoveAllShips(); // Already done in Disband()
            }
        }
    }
}