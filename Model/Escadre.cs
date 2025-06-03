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
        private bool _wasAnchorPreviouslyMoving = false;

        private int _resources;
        public int Resources { get => _resources; private set => _resources = value; }

        public event Action<int> OnResourcesChanged;
        public event Action<Formation> OnFormationChanged;
        public event Action<float> CurrentFleetSpeedChanged;
        public event Action<Vector2?> CurrentDestinationChangedEvent; // New event
        public event Action<IReadOnlyCollection<int>> TargetEscadreEntityIdsChangedEvent; // New event


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
                if (Math.Abs(previousValue - _currentFleetSpeed) > 0.01f || (previousValue == 0 && _currentFleetSpeed != 0) || (previousValue != 0 && _currentFleetSpeed == 0) )
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

            this.Position = initialPosition;
            this.Rotation = Quaternion.Identity;

            _currentFormationAnchorWorld = initialPosition;
            _activeCommandTargetWorld = initialPosition;
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
            return 50;
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
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Added Ship {ship.Id}. Total ships: {_shipEntityIds.Count}.");
                if (_shipEntityIds.Count == 1 && !_isAnchorMovingToCommandTarget)
                {
                    _currentFormationAnchorWorld = CalculateAverageShipPosition();
                    _activeCommandTargetWorld = _currentFormationAnchorWorld;
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
                    Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] All ships lost! Escadre may stop or be destroyed.");
                    _isAnchorMovingToCommandTarget = false;
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

            bool oldHadDestination = CurrentDestination.HasValue;
            Vector2? oldDestinationVal = CurrentDestination; // Capture value before change for comparison

            CurrentDestination = destination; // This will fire CurrentDestinationChangedEvent if changed

            if (!_isAnchorMovingToCommandTarget || !oldHadDestination || (oldDestinationVal.HasValue && (destination - oldDestinationVal.Value).SqrMagnitude > 0.1f) )
            {
                _currentFormationAnchorWorld = CalculateAverageShipPosition();
            }
            _isAnchorMovingToCommandTarget = true;

            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Setting course to {destination}. Anchor will move.");
            UpdateShipMovementTargets(serverTime);
        }

        public void OrderAttackEscadreEntity(int targetEscadreEntityId, float serverTime)
        {
            if (_isDisbanding || IsDead || !_shipEntityIds.Any()) return;
            if (targetEscadreEntityId == this.Id) {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Cannot target self for attack.");
                return;
            }

            if (_level.TryGetEntity(targetEscadreEntityId, out Entity targetEntity) && targetEntity is Escadre targetEscadre && !targetEscadre.IsDead)
            {
                if (_targetEscadreEntityIds.Add(targetEscadreEntityId)) {
                     Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Added attack order on escadre entity {targetEscadreEntityId}.");
                     TargetEscadreEntityIdsChangedEvent?.Invoke(new List<int>(_targetEscadreEntityIds)); // Fire event
                }
                UpdateShipMovementTargets(serverTime);
            }
            else
            {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] OrderAttackEscadreEntity: Target Escadre Entity ID {targetEscadreEntityId} not found, not an Escadre, or is dead.");
            }
        }

        public void OrderCancelAttack()
        {
            if (_isDisbanding || IsDead) return;
            if (TargetEscadreEntityIds.Any()) {
                _targetEscadreEntityIds.Clear();
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Cancelling ALL attack orders.");
                TargetEscadreEntityIdsChangedEvent?.Invoke(new List<int>(_targetEscadreEntityIds)); // Fire event
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
                _isAnchorMovingToCommandTarget = false;
                return;
            }

            bool oldIsAnchorMovingState = _isAnchorMovingToCommandTarget;
            bool newIsAnchorMovingStateThisFrame = false;

            if (CurrentDestination.HasValue)
            {
                _activeCommandTargetWorld = new Vector3(CurrentDestination.Value.X, _currentFormationAnchorWorld.Y, CurrentDestination.Value.Y);
                newIsAnchorMovingStateThisFrame = true;

                Vector3 dirToWaypoint = _activeCommandTargetWorld - _currentFormationAnchorWorld;
                if (dirToWaypoint.SqrMagnitude < (1.0f * 1.0f))
                {
                    Logger.Log($"[Escadre {Id}] Reached waypoint {CurrentDestination.Value}. Anchor snapped.");
                    _currentFormationAnchorWorld = _activeCommandTargetWorld;
                    CurrentDestination = null; // Clears waypoint, fires event
                    // newIsAnchorMovingStateThisFrame will be re-evaluated below if CurrentDestination is now null
                }
            }

            if (!CurrentDestination.HasValue && TargetEscadreEntityIds.Any())
            {
                int primaryTargetId = TargetEscadreEntityIds.First();
                if (_level.TryGetEntity(primaryTargetId, out Entity targetEntity) && targetEntity is Escadre enemyEscadre && !enemyEscadre.IsDead)
                {
                    _activeCommandTargetWorld = enemyEscadre.Position;
                    newIsAnchorMovingStateThisFrame = true;
                }
                else
                {
                    _targetEscadreEntityIds.Remove(primaryTargetId);
                    TargetEscadreEntityIdsChangedEvent?.Invoke(new List<int>(_targetEscadreEntityIds)); // Fire event
                    if (!TargetEscadreEntityIds.Any()) {
                         newIsAnchorMovingStateThisFrame = false;
                    } else {
                        newIsAnchorMovingStateThisFrame = true;
                    }
                }
            }
            // If no CurrentDestination and no TargetEscadreEntityIds, newIsAnchorMovingStateThisFrame remains false from its init.
            _isAnchorMovingToCommandTarget = newIsAnchorMovingStateThisFrame;


            if (_isAnchorMovingToCommandTarget)
            {
                if (!oldIsAnchorMovingState)
                {
                    _currentFormationAnchorWorld = CalculateAverageShipPosition();
                    Logger.Log($"[Escadre {Id}] Anchor started moving from { _currentFormationAnchorWorld} towards {_activeCommandTargetWorld}.");
                }
                Vector3 directionToCommandTarget = (_activeCommandTargetWorld - _currentFormationAnchorWorld);
                if (directionToCommandTarget.SqrMagnitude > Vector3.Epsilon)
                {
                    this.Rotation = Quaternion.LookRotation(directionToCommandTarget.NormalizedSafe(this.Rotation * Vector3.Forward), Vector3.Up);
                }
                _currentFormationAnchorWorld += this.Rotation * Vector3.Forward * CurrentFleetSpeed * deltaTime;
            }
            else
            {
                if (oldIsAnchorMovingState)
                {
                    _currentFormationAnchorWorld = CalculateAverageShipPosition();
                    _activeCommandTargetWorld = _currentFormationAnchorWorld;
                    Logger.Log($"[Escadre {Id}] Anchor stopped. Final anchor position: {_currentFormationAnchorWorld}.");
                }
                this.Rotation = CalculateAverageShipRotation();
            }

            this.Position = CalculateAverageShipPosition();

            if (_isAnchorMovingToCommandTarget)
            {
                float averageDeviation = CalculateAverageShipDeviation(_currentFormationAnchorWorld, this.Rotation);
                float normalizedDeviation = Math.Clamp(averageDeviation / MaxFormationSpreadRadius, 0f, 1f);
                float targetSpeedFactor = 1.0f - (normalizedDeviation * FormationIntegrityFactor);
                float desiredSpeed = FleetMaxSpeed * targetSpeedFactor;
                CurrentFleetSpeed = MathUtils.MoveTowards(CurrentFleetSpeed, desiredSpeed, FleetAcceleration * deltaTime);
            }
            else
            {
                CurrentFleetSpeed = MathUtils.MoveTowards(CurrentFleetSpeed, 0f, FleetAcceleration * 2f * deltaTime);
            }

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
            return _currentFormationAnchorWorld;
        }

        private Quaternion CalculateAverageShipRotation()
        {
            if (!_shipEntityIds.Any())
            {
                return this.Rotation;
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
                        averageRotation = Quaternion.Slerp(averageRotation, ship.Rotation, 1.0f / (aliveShipCount + 1.0f));
                    }
                    aliveShipCount++;
                }
            }
            return aliveShipCount > 0 ? averageRotation.Normalized : this.Rotation;
        }


        internal void UpdateShipMovementTargets(float serverTime)
        {
            if (_isDisbanding || IsDead) return;

            Quaternion fleetOrientationForFormation = this.Rotation;

            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    Vector2 relativeCenteredOffset2D = CurrentFormation.GetShipRelativeOffset(shipId);
                    Vector3 relativeScaledOffset3D = new Vector3(relativeCenteredOffset2D.X * FormationScale, 0f, relativeCenteredOffset2D.Y * FormationScale);
                    Vector3 worldOffsetFromAnchor = fleetOrientationForFormation * relativeScaledOffset3D;
                    Vector3 targetShipWorldPosition = _currentFormationAnchorWorld + worldOffsetFromAnchor;
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
            _isDisbanding = true;
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Disbanding (silentKillShips: {silentKillShips}).");

            CurrentDestination = null; // Fires event
            _targetEscadreEntityIds.Clear();
            TargetEscadreEntityIdsChangedEvent?.Invoke(new List<int>(_targetEscadreEntityIds)); // Fire event

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
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Performing Death(). Disbanding ships (non-silently).");
            Disband(false);
        }

        protected override void ObligatoryOnRemove()
        {
            base.ObligatoryOnRemove();
             Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Performing ObligatoryOnRemove().");
            if (!_isDisbanding && !IsDead)
            {
                Disband(true);
            }
            OnResourcesChanged = null;
            OnFormationChanged = null;
            CurrentFleetSpeedChanged = null;
            CurrentDestinationChangedEvent = null; // Nullify new event
            TargetEscadreEntityIdsChangedEvent = null; // Nullify new event

            if (CurrentFormation != null)
            {
                CurrentFormation.OnFormationLayoutChanged -= HandleInternalFormationLayoutChange;
                CurrentFormation.RemoveAllShips();
            }
        }
    }
}