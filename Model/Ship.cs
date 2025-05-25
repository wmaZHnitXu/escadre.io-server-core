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
            Position = initialPosition; // This might be adjusted by ocean if spawned directly on it.
            Rotation = Quaternion.Identity; 
            IsMoving = false;
            CurrentSpeed = 0f;

            this.FloatingBehavior = new DefaultFloatingBehavior(); 
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
            // We want to change the ship's commanded Yaw.
            // The final Rotation (including ocean-induced pitch/roll) is stored in this.Rotation.
            // To correctly apply yaw, we should aim to rotate around the *local* up vector
            // if the ship is tilted, or more simply, rotate around the world Y and let FloatingBehavior fix pitch/roll.
            // Let's assume for now that FloatingBehavior is dominant for pitch/roll.
            
            Vector3 currentWorldForward = Rotation * Vector3.Forward;
            Vector3 planarForward = new Vector3(currentWorldForward.X, 0, currentWorldForward.Z).Normalized;
            if (planarForward.SqrMagnitude < Vector3.Epsilon) planarForward = Vector3.Forward; // Fallback

            Quaternion currentPlanarRotation = Quaternion.LookRotation(planarForward, Vector3.Up);


            Vector2 directionToTargetPlanar = toTarget.Normalized;
            Vector3 targetForwardPlanar = new Vector3(directionToTargetPlanar.X, 0, directionToTargetPlanar.Y);
            Quaternion desiredPlanarRotation = Quaternion.LookRotation(targetForwardPlanar, Vector3.Up);
            
            Quaternion newPlanarRotation = Quaternion.RotateTowards(currentPlanarRotation, desiredPlanarRotation, TurnRate * deltaTime);

            // The new desired forward based *only* on yaw change.
            Vector3 newDesiredWorldForwardFromYaw = newPlanarRotation * Vector3.Forward;

            // Now, FloatingBehavior in the *next* Entity.Update() will use this entity's current Rotation
            // (which we are about to set) to determine its forward, then project it onto the ocean plane,
            // and then align the ship.
            // So, we set this.Rotation to be the newPlanarRotation combined with the *existing* ocean tilt.
            // This means FloatingBehavior has to work from the fully combined rotation.

            // Simpler: Set Rotation directly towards the new yaw. FloatingBehavior will correct pitch/roll.
            // This might introduce a slight coupling if FloatingBehavior heavily relies on previous frame's pitch/roll.
            // The current DefaultFloatingBehavior derives desiredForwardOnPlane from entity.Rotation * Vector3.Forward.
            // So, if we update entity.Rotation here to only reflect the new YAW, it should be fine.
            // It means the pitch/roll from the *previous* frame's ocean effect is temporarily "flattened" before
            // the *current* frame's ocean effect is applied in Entity.Update().
            
            // To preserve pitch/roll while changing yaw:
            Quaternion oldRotation = Rotation;
            Vector3 oldEuler = oldRotation.ToEulerAngles(); // Get existing pitch, yaw, roll
            Quaternion newYawOnlyRotation = Quaternion.Euler(oldEuler.X, newPlanarRotation.ToEulerAngles().Y, oldEuler.Z); // Keep old pitch/roll, apply new yaw. This is not quite right.

            // Alternative: Rotate the current full rotation towards the desired planar rotation.
            // This means the "axis" of rotation for RotateTowards will be more complex than just World Y.
            // Let's try simplest: update Rotation to have the new yaw, maintaining current up as world up for this step.
            // FloatingBehavior will then re-tilt.
            if (targetForwardPlanar.SqrMagnitude > Vector3.Epsilon)
            {
                // We want the ship to turn its "deck" (defined by its local XZ plane) towards the target.
                // The final up vector will be dictated by the ocean normal via FloatingBehavior.
                // So, Rotation should primarily reflect the yaw. FloatingBehavior will add pitch/roll.
                // This implies that `this.Rotation` might represent the "commanded" orientation,
                // and FloatingBehavior refines it.
                
                // If this.Rotation is the *final* orientation from last frame (inc. ocean),
                // we need to calculate the desired *final* orientation for *this* frame's yaw,
                // assuming ocean will tilt it appropriately.
                // Let's set Rotation to the new *planar* (yawed) orientation.
                // The FloatingBehavior in Entity.Update (which runs *before* this UpdateMovement in the same frame's sequence
                // because of base.Update()) will have already tilted the ship based on *last* frame's yaw.
                // This UpdateMovement then calculates a *new* yaw. The *next* frame's Entity.Update will apply ocean to *this* new yaw.
                Rotation = newPlanarRotation;
            }
            
            // --- Update Position (Planar) ---
            // Velocity is based on the (now yaw-updated) Rotation.
            // FloatingBehavior has already adjusted Y and potentially XZ slightly.
            // We add our planar XZ movement delta to the current position.
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