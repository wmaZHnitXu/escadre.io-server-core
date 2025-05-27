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

        protected Ship(Level level, Escadre ownerEscadre, Vector3 initialPosition, float maxHealth)
            : base(level, maxHealth)
        {
            OwningEscadre = ownerEscadre ?? throw new ArgumentNullException(nameof(ownerEscadre));
            OwningEscadreClientId = ownerEscadre.OwnerClientId;
            Position = initialPosition; 
            Rotation = Quaternion.Identity;
            IsMoving = false;
            CurrentSpeed = 0f;

            // DefaultFloatingBehavior is set by concrete ship class, e.g. DefaultShip constructor
            // This ensures that derived classes can specify their own floating points.
        }

        public override void Update(float delta)
        {
            // Entity.Update (which handles FloatingBehavior) will be called first by DestructibleEntity's base.Update().
            // This will adjust Position.Y and the full Rotation based on ocean.
            base.Update(delta); 

            if (IsDead) return;

            // UpdateMovement then handles planar (XZ) movement and Yaw adjustments.
            // It reads the (potentially ocean-modified) Position and Rotation,
            // but primarily acts on XZ components and Yaw for its logic.
            UpdateMovement(delta);
        }

        public void SetMovementTarget(Vector2? targetWorldPosition, float serverTime)
        {
            bool changed = (_movementTargetPosition.HasValue != targetWorldPosition.HasValue) ||
                           (targetWorldPosition.HasValue && _movementTargetPosition.Value != targetWorldPosition.Value);

            _movementTargetPosition = targetWorldPosition;

            if (targetWorldPosition.HasValue)
            {
                IsMoving = true;
            }
            else
            {
                 IsMoving = false; 
            }

            if (changed || targetWorldPosition.HasValue)
            {
                OnMovementTargetProgrammed?.Invoke(this, _movementTargetPosition, serverTime);
            }
        }


        protected virtual void UpdateMovement(float deltaTime)
        {
            // This movement logic primarily controls the ship's XZ position and its Yaw (rotation around Y-axis).
            // The FloatingBehavior (called in base.Update()) handles Y position and Pitch/Roll.

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
                // Don't snap XZ position here. Let deceleration and ocean behavior finalize.
                return;
            }

            if (CurrentSpeed < MaxSpeed)
            {
                CurrentSpeed = Math.Min(MaxSpeed, CurrentSpeed + (MaxSpeed * 1.0f * deltaTime)); 
            } else {
                 CurrentSpeed = MaxSpeed;
            }
            
            // --- Yaw Rotation ---
            // The ship's commanded yaw. FloatingBehavior handles the ocean-induced pitch/roll.
            // We extract the current planar forward from the full Rotation.
            Vector3 currentWorldForward = Rotation * Vector3.Forward; // Current orientation including ocean effects
            Vector3 planarForward = new Vector3(currentWorldForward.X, 0, currentWorldForward.Z).NormalizedSafe(Vector3.Forward);
            Quaternion currentPlanarRotation = Quaternion.LookRotation(planarForward, Vector3.Up);

            Vector2 directionToTargetPlanar = toTarget.Normalized;
            Vector3 targetForwardPlanar = new Vector3(directionToTargetPlanar.X, 0, directionToTargetPlanar.Y);
            
            Quaternion newPlanarRotation = currentPlanarRotation; // Default to current if target is directly behind or too close
            if (targetForwardPlanar.SqrMagnitude > Vector3.Epsilon) // Ensure targetForward is not zero
            {
                Quaternion desiredPlanarRotation = Quaternion.LookRotation(targetForwardPlanar, Vector3.Up);
                newPlanarRotation = Quaternion.RotateTowards(currentPlanarRotation, desiredPlanarRotation, TurnRate * deltaTime);
            }

            // We set the Rotation to this newPlanarRotation.
            // The FloatingBehavior (which ran *before* this UpdateMovement in the same frame via base.Update())
            // has already applied pitch/roll based on the *previous* frame's Rotation.
            // Now, we update Rotation with the new commanded yaw. The *next* frame's FloatingBehavior
            // will use this updated Rotation (new yaw, but potentially "flattened" pitch/roll from this assignment)
            // as its basis for calculating new pitch/roll.
            Rotation = newPlanarRotation;
            
            // --- Update Position (Planar) ---
            // Velocity is based on the (now yaw-updated) Rotation.
            Vector3 planarVelocityDelta = Rotation * Vector3.Forward * CurrentSpeed * deltaTime;
            Position = new Vector3(Position.X + planarVelocityDelta.X, Position.Y, Position.Z + planarVelocityDelta.Z);
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