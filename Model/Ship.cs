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
            base.Update(delta); // Handles FloatingBehavior

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
            IsMoving = targetWorldPosition.HasValue; 

            if (changed || targetWorldPosition.HasValue)
            {
                OnMovementTargetProgrammed?.Invoke(this, _movementTargetPosition, serverTime);
            }
        }

        protected virtual void UpdateMovement(float deltaTime, Quaternion currentFullRotationFromOcean)
        {
            if (!IsMoving || !_movementTargetPosition.HasValue)
            {
                if (CurrentSpeed > 0)
                {
                    CurrentSpeed = Math.Max(0, CurrentSpeed - (MaxSpeed * 2f * deltaTime)); 
                }
                else { CurrentSpeed = 0f; }
                if (CurrentSpeed == 0f) IsMoving = false; 
                return;
            }

            Vector2 currentPos2D = new Vector2(Position.X, Position.Z); 
            Vector2 targetPos2D = _movementTargetPosition.Value;
            Vector2 toTarget = targetPos2D - currentPos2D;

            float distanceToTargetSq = toTarget.SqrMagnitude;
            
            float dynamicStoppingDistance = CurrentSpeed * deltaTime * 0.75f; 
            dynamicStoppingDistance = Math.Max(MaxSpeed * deltaTime * 0.25f, dynamicStoppingDistance); 
            float stoppingDistanceSq = dynamicStoppingDistance * dynamicStoppingDistance;
            stoppingDistanceSq = Math.Max(0.01f * 0.01f, stoppingDistanceSq); 

            if (distanceToTargetSq < stoppingDistanceSq)
            {
                IsMoving = false; 
                return;
            }

            if (CurrentSpeed < MaxSpeed)
            {
                CurrentSpeed = Math.Min(MaxSpeed, CurrentSpeed + (MaxSpeed * 1.0f * deltaTime)); 
            } else {
                 CurrentSpeed = MaxSpeed;
            }
            
            Vector3 currentWorldForwardFromOcean = currentFullRotationFromOcean * Vector3.Forward;
            Vector3 planarForwardFromOcean = new Vector3(currentWorldForwardFromOcean.X, 0, currentWorldForwardFromOcean.Z).NormalizedSafe(Vector3.Forward);
            Quaternion currentPlanarYawComponent = Quaternion.LookRotation(planarForwardFromOcean, Vector3.Up);

            Vector2 directionToTargetPlanar = toTarget.Normalized;
            Vector3 targetForwardPlanar = new Vector3(directionToTargetPlanar.X, 0, directionToTargetPlanar.Y); 
            
            Quaternion desiredPureYawRotation = currentPlanarYawComponent; 
            if (targetForwardPlanar.SqrMagnitude > Vector3.Epsilon) 
            {
                desiredPureYawRotation = Quaternion.LookRotation(targetForwardPlanar, Vector3.Up);
            }

            Quaternion newPureYawComponent = Quaternion.RotateTowards(currentPlanarYawComponent, desiredPureYawRotation, TurnRate * deltaTime);
            Quaternion yawChange = newPureYawComponent * currentPlanarYawComponent.Inverse;
            this.Rotation = (yawChange * currentFullRotationFromOcean).Normalized; 
            
            Vector3 planarVelocityDelta = newPureYawComponent * Vector3.Forward * CurrentSpeed * deltaTime;
            this.Position = new Vector3(Position.X + planarVelocityDelta.X, Position.Y, Position.Z + planarVelocityDelta.Z);
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

            // Use spatial query from Level
            var nearbyCollectables = _level.GetEntitiesInRadius(
                new Vector2(this.Position.X, this.Position.Z),
                this.CollectableDetectionRange,
                entity => entity is CollectableFloatingEntity cfe && !cfe.IsDead && cfe.CollectingShip == null
            ).OfType<CollectableFloatingEntity>();


            foreach (var collectable in nearbyCollectables)
            {
                // Redundant checks if filter is perfect, but good for safety
                if (collectable.IsDead || collectable.CollectingShip != null) continue;

                float distSq = (collectable.Position - this.Position).SqrMagnitude;
                // QueryRadius should ensure they are within CollectableDetectionRange,
                // but this precise distSq is still useful for finding the *closest*.
                if (distSq <= closestDistSq) // Check against current closest, not just detection range
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
    }
}