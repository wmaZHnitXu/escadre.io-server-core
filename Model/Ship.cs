// File: Core/Model/Ship.cs
using System;
using Core.Primitives;
using Core.Logging;
using System.Linq;
using System.Collections.Generic;
using Core.Ocean;
namespace Core.Model
{
    public abstract class Ship : DestructibleEntity
    {
        public int OwningEscadreClientId { get; }
        public Escadre OwningEscadre { get; }
        public float CurrentSpeed { get; protected set; }
        public bool IsMoving { get; protected set; }

        public abstract float MaxSpeed { get; protected set; }
        public abstract float TurnRate { get; protected set; }

        private Vector2? _movementTargetPosition;
        public event Action<Ship, Vector2?, float> OnMovementTargetProgrammed;

        protected readonly List<DefaultCannon> _cannons = new List<DefaultCannon>();
        public IReadOnlyList<DefaultCannon> Cannons => _cannons.AsReadOnly();

        public float CollectableDetectionRange { get; protected set; } = 7.0f;
        private CollectableFloatingEntity _targetedCollectable = null;

        public float SlowingDistance { get; protected set; }
        public float StoppingDistance { get; protected set; }
        public float FormationThreshold { get; protected set; }
        public float AccelerationRate { get; protected set; }
        public float DecelerationRate { get; protected set; }


        protected Ship(Level level, Escadre ownerEscadre, Vector3 initialPosition, float maxHealth)
            : base(level, maxHealth)
        {
            OwningEscadre = ownerEscadre ?? throw new ArgumentNullException(nameof(ownerEscadre));
            OwningEscadreClientId = ownerEscadre.OwnerClientId;
            Position = initialPosition;
            Rotation = Quaternion.Identity;
            IsMoving = false;
            CurrentSpeed = 0f;

            SlowingDistance = 3.0f;
            StoppingDistance = 0.5f;
            FormationThreshold = 2.0f;
            AccelerationRate = 2.0f;
            DecelerationRate = 8.0f;
        }

        public override void Update(float delta)
        {
            base.Update(delta);

            if (IsDead)
            {
                if (_targetedCollectable != null && _targetedCollectable.CollectingShip == this)
                {
                    _targetedCollectable.Unclaim();
                    _targetedCollectable = null;
                }
                return;
            }

            UpdateMovement(delta, this.Rotation);
            UpdateCollectablesInteraction(delta);
        }

        public void SetMovementTarget(Vector2? targetWorldPosition, float serverTime)
        {
            bool changed = (_movementTargetPosition.HasValue != targetWorldPosition.HasValue) ||
                           (targetWorldPosition.HasValue && _movementTargetPosition.Value != targetWorldPosition.Value);

            _movementTargetPosition = targetWorldPosition;

            if (targetWorldPosition.HasValue)
            {
                // Check if the new target is significantly different from current position
                Vector2 currentPos2D = new Vector2(Position.X, Position.Z);
                if ((targetWorldPosition.Value - currentPos2D).SqrMagnitude > StoppingDistance * StoppingDistance * 0.8f) // Be a bit lenient
                {
                    IsMoving = true;
                }
                // else, if target is very close, IsMoving might remain false or be set false in UpdateMovement
            }
            // If target is cleared (!targetWorldPosition.HasValue), UpdateMovement will handle stopping and setting IsMoving = false.


            if (changed || targetWorldPosition.HasValue)
            {
                OnMovementTargetProgrammed?.Invoke(this, _movementTargetPosition, serverTime);
            }
        }

        protected virtual void UpdateMovement(float deltaTime, Quaternion currentFullRotationFromOcean)
        {
            float targetSpeedThisFrame;

            if (!_movementTargetPosition.HasValue)
            {
                targetSpeedThisFrame = 0f;
            }
            else
            {
                Vector2 currentPos2D = new Vector2(Position.X, Position.Z);
                Vector2 targetSlotPos2D = _movementTargetPosition.Value;
                Vector2 toTargetSlot = targetSlotPos2D - currentPos2D;
                float distanceToSlot = toTargetSlot.Magnitude;

                float effectiveDecelForStopping = DecelerationRate;
                if (effectiveDecelForStopping < Vector3.Epsilon) effectiveDecelForStopping = 1f;
                float predictiveStopDist = (CurrentSpeed * CurrentSpeed) / (2f * effectiveDecelForStopping);
                float dynamicStoppingThreshold = Math.Max(predictiveStopDist, StoppingDistance) + 0.1f;


                if (distanceToSlot < dynamicStoppingThreshold)
                {
                    targetSpeedThisFrame = 0f;
                }
                else if (distanceToSlot < SlowingDistance)
                {
                    float baseSpeedForSlowing = MaxSpeed;
                    if (!OwningEscadre.IsDead && (OwningEscadre.CurrentDestination.HasValue || OwningEscadre.TargetEscadreEntityIds.Any()))
                    {
                        baseSpeedForSlowing = Math.Min(MaxSpeed, OwningEscadre.CurrentFleetSpeed * 1.2f);
                    }
                    float slowingFactor = Math.Clamp(distanceToSlot / Math.Max(SlowingDistance, 0.1f), 0.1f, 1.0f);
                    targetSpeedThisFrame = baseSpeedForSlowing * slowingFactor;
                    targetSpeedThisFrame = Math.Max(0, targetSpeedThisFrame);
                }
                else
                {
                    float escadreRefSpeed = OwningEscadre.IsDead ? MaxSpeed : OwningEscadre.CurrentFleetSpeed;
                    bool escadreIsActivelyMoving = !OwningEscadre.IsDead && OwningEscadre.CurrentFleetSpeed > 0.05f && // slightly lower threshold for escadre active
                                                 (OwningEscadre.CurrentDestination.HasValue || OwningEscadre.TargetEscadreEntityIds.Any());
                    float formationDeviation = distanceToSlot;

                    if (formationDeviation > FormationThreshold && escadreIsActivelyMoving)
                    {
                        targetSpeedThisFrame = Math.Min(MaxSpeed * 1.2f, escadreRefSpeed * 1.5f + MaxSpeed * 0.5f);
                    }
                    else if (escadreIsActivelyMoving)
                    {
                        targetSpeedThisFrame = Math.Min(MaxSpeed, escadreRefSpeed + (formationDeviation / Math.Max(FormationThreshold, 0.1f)) * MaxSpeed * 0.5f);
                    }
                    else // Escadre is holding or very slow
                    {
                        // If escadre is holding, ship should move to its designated slot and then stop.
                        // Target speed should allow it to reach the slot.
                        targetSpeedThisFrame = MaxSpeed * 0.75f;
                        // If already close to slot while escadre holding, this speed will be reduced by slowing/stopping logic.
                    }
                }
                targetSpeedThisFrame = Math.Min(targetSpeedThisFrame, MaxSpeed * 1.25f);
            }

            if (targetSpeedThisFrame > CurrentSpeed)
            {
                CurrentSpeed = MathUtils.MoveTowards(CurrentSpeed, targetSpeedThisFrame, AccelerationRate * deltaTime);
            }
            else
            {
                CurrentSpeed = MathUtils.MoveTowards(CurrentSpeed, targetSpeedThisFrame, DecelerationRate * deltaTime);
            }

            // Update IsMoving state
            if (CurrentSpeed < 0.01f)
            {
                CurrentSpeed = 0f;
                bool atTargetSlot = false;
                if (_movementTargetPosition.HasValue)
                {
                    Vector2 currentPos2D = new Vector2(Position.X, Position.Z);
                    if ((_movementTargetPosition.Value - currentPos2D).SqrMagnitude < (StoppingDistance + 0.2f) * (StoppingDistance + 0.2f))
                    {
                        atTargetSlot = true;
                    }
                }

                if (!_movementTargetPosition.HasValue || atTargetSlot)
                { // No target, or at current target
                    IsMoving = false;
                }
                else
                { // Has a target, speed is zero, but not yet at the target
                    IsMoving = true;
                }
            }
            else
            { // Speed is significant
                IsMoving = true;
            }


            // --- Rotation and Position Update ---
            if (IsMoving || CurrentSpeed > 0.001f) // Rotate and move if IsMoving flag is true or still has residual speed
            {
                Quaternion finalRotation = currentFullRotationFromOcean;
                if (_movementTargetPosition.HasValue)
                {
                    Vector2 currentPos2D = new Vector2(Position.X, Position.Z);
                    Vector2 toTargetSlot = _movementTargetPosition.Value - currentPos2D;

                    if (toTargetSlot.SqrMagnitude > 0.01f)
                    {
                        Vector3 currentWorldForwardFromOcean = currentFullRotationFromOcean * Vector3.Forward;
                        Vector3 planarForwardFromOcean = new Vector3(currentWorldForwardFromOcean.X, 0f, currentWorldForwardFromOcean.Z).NormalizedSafe(Vector3.Forward);
                        Quaternion currentPlanarYawComponent = Quaternion.LookRotation(planarForwardFromOcean, Vector3.Up);

                        Vector2 directionToTargetPlanar = toTargetSlot.Normalized;
                        Vector3 targetForwardPlanar = new Vector3(directionToTargetPlanar.X, 0f, directionToTargetPlanar.Y);

                        Quaternion desiredPureYawRotation = currentPlanarYawComponent;
                        if (targetForwardPlanar.SqrMagnitude > Vector3.Epsilon)
                        {
                            desiredPureYawRotation = Quaternion.LookRotation(targetForwardPlanar, Vector3.Up);
                        }
                        Quaternion newPureYawComponent = Quaternion.RotateTowards(currentPlanarYawComponent, desiredPureYawRotation, TurnRate * deltaTime);
                        Quaternion yawChange = newPureYawComponent * currentPlanarYawComponent.Inverse;
                        finalRotation = (yawChange * currentFullRotationFromOcean).Normalized;
                    }
                }
                this.Rotation = finalRotation;

                if (CurrentSpeed > 0f)
                {
                    Vector3 finalPlanarForward = (this.Rotation * Vector3.Forward);
                    finalPlanarForward = new Vector3(finalPlanarForward.X, 0f, finalPlanarForward.Z).NormalizedSafe(Vector3.Forward);
                    Vector3 planarVelocityDelta = finalPlanarForward * CurrentSpeed * deltaTime;
                    this.Position = new Vector3(Position.X + planarVelocityDelta.X, Position.Y, Position.Z + planarVelocityDelta.Z);
                }
            }
        }


        protected virtual void UpdateCollectablesInteraction(float deltaTime)
        {
            if (IsDead) return;

            if (_targetedCollectable != null)
            {
                if (_targetedCollectable.IsDead || _targetedCollectable.CollectingShip != this)
                {
                    _targetedCollectable = null;
                }
                else
                {
                    return;
                }
            }

            CollectableFloatingEntity closestUnclaimedCollectable = null;
            float closestDistSq = CollectableDetectionRange * CollectableDetectionRange;

            var nearbyCollectables = _level.GetEntitiesInRadius(
                new Vector2(this.Position.X, this.Position.Z),
                this.CollectableDetectionRange,
                entity => entity is CollectableFloatingEntity cfe && !cfe.IsDead && cfe.CollectingShip == null
            ).OfType<CollectableFloatingEntity>();


            foreach (var collectable in nearbyCollectables)
            {
                if (collectable.IsDead || collectable.CollectingShip != null) continue;
                float distSq = (collectable.Position - this.Position).SqrMagnitude;
                if (distSq <= closestDistSq)
                {
                    closestDistSq = distSq;
                    closestUnclaimedCollectable = collectable;
                }
            }

            if (closestUnclaimedCollectable != null)
            {
                if (closestUnclaimedCollectable.TryClaimBy(this))
                {
                    _targetedCollectable = closestUnclaimedCollectable;
                }
            }
        }


        protected override void Death()
        {
            base.Death();
            if (OwningEscadre != null && !OwningEscadre.IsDead)
            {
                OwningEscadre.HandleShipDestroyed(this.Id);
            }
            Logger.Log($"[Ship {Id}] Ship Death() processed. Notified Escadre {OwningEscadre?.Id}.");
        }

        public virtual void PerformUpgrade()
        {
            Logger.Log($"[Ship {Id}] Base PerformUpgrade called. No changes by default.");
        }

        protected override void ObligatoryOnRemove()
        {
            base.ObligatoryOnRemove();
            OnMovementTargetProgrammed = null;

            if (_targetedCollectable != null && _targetedCollectable.CollectingShip == this && !_targetedCollectable.IsDead)
            {
                _targetedCollectable.Unclaim();
            }
            _targetedCollectable = null;

            var cannonsToKill = new List<DefaultCannon>(_cannons);
            foreach (var cannon in cannonsToKill)
            {
                if (cannon != null && !cannon.IsDead)
                {
                    cannon.Kill(true);
                }
            }
            _cannons.Clear();
        }

        /// <summary>
        /// Calculates the distance this ship would need to stop from its current speed.
        /// Includes a small buffer.
        /// </summary>
        /// <returns>The predicted stopping distance based on current speed and deceleration rate.</returns>
        public float GetPredictiveStoppingDistance()
        {
            if (DecelerationRate < Vector3.Epsilon) return StoppingDistance; // Fallback to configured one if decel is near zero
                                                                             // d = v^2 / (2*a)
            return (CurrentSpeed * CurrentSpeed) / (2f * DecelerationRate) + 0.1f; // Added small buffer
        }
    }
}