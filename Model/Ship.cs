// File: Core/Model/Ship.cs
using System;
using Core.Primitives;
using Core.Logging;
using System.Linq;
using System.Collections.Generic; // Added for List<DefaultCannon>

namespace Core.Model
{
    public abstract class Ship : DestructibleEntity
    {
        public int OwningEscadreClientId { get; } // Client ID of the owner of the Escadre this ship belongs to
        public Escadre OwningEscadre { get; } // The Escadre Entity this ship belongs to

        public float CurrentSpeed { get; protected set; }
        public bool IsMoving { get; protected set; }

        public abstract float MaxSpeed { get; protected set; }
        public abstract float TurnRate { get; protected set; }
        
        private Vector2? _movementTargetPosition; 
        public event Action<Ship, Vector2?, float> OnMovementTargetProgrammed; 

        protected readonly List<DefaultCannon> _cannons = new List<DefaultCannon>();
        public IReadOnlyList<DefaultCannon> Cannons => _cannons.AsReadOnly();

        protected Ship(Level level, Escadre ownerEscadre, Vector3 initialPosition, float maxHealth)
            : base(level, maxHealth)
        {
            OwningEscadre = ownerEscadre ?? throw new ArgumentNullException(nameof(ownerEscadre));
            OwningEscadreClientId = ownerEscadre.OwnerClientId; // Get OwnerClientId from the Escadre entity
            Position = initialPosition;
            Rotation = Quaternion.Identity; 
            IsMoving = false;
            CurrentSpeed = 0f;
        }

        public override void Update(float delta)
        {
            base.Update(delta); 

            if (IsDead) return;

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
                 CurrentSpeed = 0f; 
            }

            if (changed || targetWorldPosition.HasValue)
            {
                OnMovementTargetProgrammed?.Invoke(this, _movementTargetPosition, serverTime);
            }
        }


        protected virtual void UpdateMovement(float deltaTime)
        {
            if (!IsMoving || !_movementTargetPosition.HasValue)
            {
                if (CurrentSpeed > 0)
                {
                    CurrentSpeed = Math.Max(0, CurrentSpeed - (MaxSpeed * 2f * deltaTime)); 
                }
                else { CurrentSpeed = 0f; }
                IsMoving = false; 
                return;
            }

            Vector2 currentPos2D = new Vector2(Position.X, Position.Z);
            Vector2 targetPos2D = _movementTargetPosition.Value;
            Vector2 toTarget = targetPos2D - currentPos2D;

            float distanceToTargetSq = toTarget.SqrMagnitude;
            
            float stoppingDistance = MaxSpeed * deltaTime * 0.5f; 
            float stoppingDistanceSq = stoppingDistance * stoppingDistance;
            stoppingDistanceSq = Math.Max(0.0025f, stoppingDistanceSq); 

            if (distanceToTargetSq < stoppingDistanceSq)
            {
                IsMoving = false; 
                CurrentSpeed = 0f;
                Position = new Vector3(targetPos2D.X, Position.Y, targetPos2D.Y); 
                return;
            }

            if (CurrentSpeed < MaxSpeed)
            {
                CurrentSpeed = Math.Min(MaxSpeed, CurrentSpeed + (MaxSpeed * 1.0f * deltaTime)); 
            } else {
                 CurrentSpeed = MaxSpeed;
            }

            Vector2 directionToTarget = toTarget.Normalized;
            Vector3 targetForward = new Vector3(directionToTarget.X, 0, directionToTarget.Y);

            if (targetForward.SqrMagnitude > Vector3.Epsilon) 
            {
                Quaternion desiredRotation = Quaternion.LookRotation(targetForward, Vector3.Up);
                Rotation = Quaternion.RotateTowards(Rotation, desiredRotation, TurnRate * deltaTime);
            }
            
            Vector3 velocity = Rotation * Vector3.Forward * CurrentSpeed * deltaTime;
            Position += velocity;
        }

        protected override void Death()
        {
            base.Death(); // Call base DestructibleEntity.Death

            // Notify the owning escadre that this ship has been destroyed
            if (OwningEscadre != null && !OwningEscadre.IsDead) // Check if escadre still exists
            {
                OwningEscadre.HandleShipDestroyed(this.Id);
            }
            Logger.Log($"[Ship {Id}] Ship Death() processed. Notified Escadre {OwningEscadre?.Id}. An additional call to kill cannons will be made in ObligatoryOnRemove.");
        }

        public virtual void PerformUpgrade()
        {
            Logger.Log($"[Ship {Id}] Base PerformUpgrade called. No changes by default.");
            // Derived classes will implement specific stat changes.
            // Upgrading cannons would happen here too, e.g. by iterating _cannons list.
        }

        protected override void ObligatoryOnRemove()
        {
            base.ObligatoryOnRemove();
            OnMovementTargetProgrammed = null; // Clear events
            
            // Kill all attached cannons when the ship is removed
            // Create a copy of the list for safe iteration if Kill() modifies the collection indirectly.
            var cannonsToKill = new List<DefaultCannon>(_cannons);
            foreach (var cannon in cannonsToKill)
            {
                if (cannon != null && !cannon.IsDead)
                {
                    cannon.Kill(true); // Kill silently as the ship's removal is the primary event
                }
            }
            _cannons.Clear();
            // The OwningEscadre will handle removing this ship from its lists when the ship dies.
        }
    }
}