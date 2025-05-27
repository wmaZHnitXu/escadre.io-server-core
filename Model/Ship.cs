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

        public float CurrentSpeed { get; protected set; } // Planar speed
        public bool IsMoving { get; protected set; }

        public abstract float MaxSpeed { get; protected set; }
        public abstract float TurnRate { get; protected set; } // Yaw rate
        
        private Vector2? _movementTargetPosition; 
        public event Action<Ship, Vector2?, float> OnMovementTargetProgrammed; 

        protected readonly List<DefaultCannon> _cannons = new List<DefaultCannon>();
        public IReadOnlyList<DefaultCannon> Cannons => _cannons.AsReadOnly();

        public float CollectableDetectionRange { get; protected set; } = 7.0f;
        private CollectableFloatingEntity _targetedCollectable = null; 

        protected Ship(Level level, Escadre ownerEscadre, Vector3 initialPosition, float maxHealth)
            : base(level, maxHealth)
        {
            OwningEscadre = ownerEscadre ?? throw new ArgumentNullException(nameof(ownerEscadre));
            OwningEscadreClientId = ownerEscadre.OwnerClientId;
            Position = initialPosition; 
            Rotation = Quaternion.Identity;
            IsMoving = false;
            CurrentSpeed = 0f;
        }

        public override void Update(float delta)
        {
            base.Update(delta); // This calls Entity.Update -> FloatingBehavior.ApplyFloating
                                // this.Position and this.Rotation are now updated by FloatingBehavior

            if (IsDead)
            {
                if (_targetedCollectable != null && _targetedCollectable.CollectingShip == this)
                {
                    _targetedCollectable.Unclaim();
                    _targetedCollectable = null;
                }
                return;
            }

            // Pass the current, ocean-affected rotation to UpdateMovement
            UpdateMovement(delta, this.Rotation); 
            UpdateCollectablesInteraction(delta);
        }

        public void SetMovementTarget(Vector2? targetWorldPosition, float serverTime)
        {
            bool changed = (_movementTargetPosition.HasValue != targetWorldPosition.HasValue) ||
                           (targetWorldPosition.HasValue && _movementTargetPosition.Value != targetWorldPosition.Value);

            _movementTargetPosition = targetWorldPosition;

            IsMoving = targetWorldPosition.HasValue; // Set IsMoving based on whether a target exists

            if (changed || targetWorldPosition.HasValue)
            {
                OnMovementTargetProgrammed?.Invoke(this, _movementTargetPosition, serverTime);
            }
        }

        // Method signature changed to accept currentFullRotationFromOcean
        protected virtual void UpdateMovement(float deltaTime, Quaternion currentFullRotationFromOcean)
        {
            if (!IsMoving || !_movementTargetPosition.HasValue)
            {
                if (CurrentSpeed > 0)
                {
                    CurrentSpeed = Math.Max(0, CurrentSpeed - (MaxSpeed * 2f * deltaTime)); 
                }
                else { CurrentSpeed = 0f; }
                if (CurrentSpeed == 0f) IsMoving = false; // Ensure IsMoving is false if speed is zero
                return;
            }

            Vector2 currentPos2D = new Vector2(Position.X, Position.Z); // Position.Y is already ocean-adjusted
            Vector2 targetPos2D = _movementTargetPosition.Value;
            Vector2 toTarget = targetPos2D - currentPos2D;

            float distanceToTargetSq = toTarget.SqrMagnitude;
            
            float dynamicStoppingDistance = CurrentSpeed * deltaTime * 0.75f; 
            dynamicStoppingDistance = Math.Max(MaxSpeed * deltaTime * 0.25f, dynamicStoppingDistance); // Min stopping distance related to max speed
            float stoppingDistanceSq = dynamicStoppingDistance * dynamicStoppingDistance;
            stoppingDistanceSq = Math.Max(0.01f * 0.01f, stoppingDistanceSq); // Ensure a very small minimum stopping distance squared

            if (distanceToTargetSq < stoppingDistanceSq)
            {
                IsMoving = false; // Reached target or close enough to stop active movement
                // CurrentSpeed will naturally decrease in the next frame if IsMoving is false.
                return;
            }

            // Acceleration
            if (CurrentSpeed < MaxSpeed)
            {
                CurrentSpeed = Math.Min(MaxSpeed, CurrentSpeed + (MaxSpeed * 1.0f * deltaTime)); 
            } else {
                 CurrentSpeed = MaxSpeed;
            }
            
            // --- Yaw Rotation ---
            // currentFullRotationFromOcean already includes pitch/roll from the ocean.
            // We extract its current planar forward direction to determine current heading for yaw calculations.
            Vector3 currentWorldForwardFromOcean = currentFullRotationFromOcean * Vector3.Forward;
            Vector3 planarForwardFromOcean = new Vector3(currentWorldForwardFromOcean.X, 0, currentWorldForwardFromOcean.Z).NormalizedSafe(Vector3.Forward);
            Quaternion currentPlanarYawComponent = Quaternion.LookRotation(planarForwardFromOcean, Vector3.Up);

            // Determine desired planar forward based on movement target
            Vector2 directionToTargetPlanar = toTarget.Normalized;
            // Vector2 is (X,Y), map Y to Z for Vector3 world space
            Vector3 targetForwardPlanar = new Vector3(directionToTargetPlanar.X, 0, directionToTargetPlanar.Y); 
            
            Quaternion desiredPureYawRotation = currentPlanarYawComponent; // Default to current if target is invalid (e.g. zero vector)
            if (targetForwardPlanar.SqrMagnitude > Vector3.Epsilon) 
            {
                desiredPureYawRotation = Quaternion.LookRotation(targetForwardPlanar, Vector3.Up);
            }

            // Interpolate the pure yaw component
            Quaternion newPureYawComponent = Quaternion.RotateTowards(currentPlanarYawComponent, desiredPureYawRotation, TurnRate * deltaTime);

            // Calculate the change in yaw and apply it to the ocean-influenced rotation
            Quaternion yawChange = newPureYawComponent * currentPlanarYawComponent.Inverse;
            this.Rotation = (yawChange * currentFullRotationFromOcean).Normalized; // Apply yaw change and normalize
            
            // --- Update Position (Planar) ---
            // Velocity is based on the commanded yaw (newPureYawComponent) to ensure XZ movement is planar.
            // Position.Y is already set by FloatingBehavior. We only modify X and Z.
            Vector3 planarVelocityDelta = newPureYawComponent * Vector3.Forward * CurrentSpeed * deltaTime;
            this.Position = new Vector3(Position.X + planarVelocityDelta.X, Position.Y, Position.Z + planarVelocityDelta.Z);
        }

        protected virtual void UpdateCollectablesInteraction(float deltaTime)
        {
            if (IsDead) return;

            // If already targeting a collectable, check if it's still valid or collected
            if (_targetedCollectable != null)
            {
                if (_targetedCollectable.IsDead || _targetedCollectable.CollectingShip != this)
                {
                    // It was collected by us (and isDead now), or someone else claimed it, or it died.
                    _targetedCollectable = null;
                }
                else
                {
                    return; // Still actively attracting this one, don't look for others.
                }
            }

            // Scan for new collectables if not currently attracting one
            CollectableFloatingEntity closestUnclaimedCollectable = null;
            float closestDistSq = CollectableDetectionRange * CollectableDetectionRange;

            foreach (var entity in _level.GetAllEntities().OfType<CollectableFloatingEntity>())
            {
                if (entity.IsDead || entity.CollectingShip != null) // Skip dead or already claimed
                {
                    continue;
                }

                float distSq = (entity.Position - this.Position).SqrMagnitude;
                if (distSq <= closestDistSq)
                {
                    closestDistSq = distSq;
                    closestUnclaimedCollectable = entity;
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
            
            // If ship is removed for reasons other than death (e.g. disband), ensure collectable is unclaimed
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
    }
}