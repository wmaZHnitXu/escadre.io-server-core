// File: Core/Model/Ship.cs
using System;
using Core.Primitives;
using Core.Logging;
using System.Linq;

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
        public abstract float AttackDamage { get; protected set; }
        public abstract float AttackRange { get; protected set; }
        public abstract float AttackCooldown { get; protected set; }
        
        private Vector2? _movementTargetPosition; 
        public event Action<Ship, Vector2?, float> OnMovementTargetProgrammed; 

        protected float _currentAttackCooldownTimer;

        protected Ship(Level level, Escadre ownerEscadre, Vector3 initialPosition, float maxHealth)
            : base(level, maxHealth)
        {
            OwningEscadre = ownerEscadre ?? throw new ArgumentNullException(nameof(ownerEscadre));
            OwningEscadreClientId = ownerEscadre.OwnerClientId; // Get OwnerClientId from the Escadre entity
            Position = initialPosition;
            Rotation = Quaternion.Identity; 
            IsMoving = false;
            CurrentSpeed = 0f;
            _currentAttackCooldownTimer = 0f;
        }

        public override void Update(float delta)
        {
            base.Update(delta); 

            if (IsDead) return;

            UpdateMovement(delta);
            UpdateAttack(delta);
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

        protected virtual void UpdateAttack(float deltaTime)
        {
            if (_currentAttackCooldownTimer > 0)
            {
                _currentAttackCooldownTimer -= deltaTime;
            }

            if (IsDead || _currentAttackCooldownTimer > 0 || OwningEscadre == null || OwningEscadre.IsDead) return;

            // Use TargetEscadreEntityIds from the OwningEscadre Entity
            if (!OwningEscadre.TargetEscadreEntityIds.Any()) return;

            Ship targetShip = FindBestTarget();

            if (targetShip != null)
            {
                PerformAttackOn(targetShip);
                _currentAttackCooldownTimer = AttackCooldown;
            }
        }

        protected Ship FindBestTarget()
        {
            if (OwningEscadre == null || OwningEscadre.IsDead) return null;

            Ship bestTarget = null;
            float closestDistSq = AttackRange * AttackRange;

            // Iterate through the target Escadre Entity IDs stored in the OwningEscadre
            foreach (int targetEscadreEntityId in OwningEscadre.TargetEscadreEntityIds)
            {
                if (_level.TryGetEntity(targetEscadreEntityId, out Entity targetEntity) && targetEntity is Escadre targetEscadre && !targetEscadre.IsDead)
                {
                    foreach (int enemyShipId in targetEscadre.ShipEntityIds)
                    {
                        if (_level.TryGetEntity(enemyShipId, out Entity enemyEntity) && enemyEntity is Ship enemyShip && !enemyShip.IsDead)
                        {
                            float distSq = (enemyShip.Position - Position).SqrMagnitude;
                            if (distSq <= closestDistSq)
                            {
                                // TODO: Line of Sight Check
                                closestDistSq = distSq;
                                bestTarget = enemyShip;
                            }
                        }
                    }
                }
                // If a primary target escadre yields a bestTarget, we can break early or continue searching other target escadres.
                // For simplicity, let's assume the first target escadre in the list is prioritized.
                // If we want to pick the absolute closest ship from *any* targeted escadre, remove this 'if bestTarget != null break;'
                if (bestTarget != null) break; 
            }
            return bestTarget;
        }

        protected virtual void PerformAttackOn(Ship target)
        {
            Logger.Log($"[Ship {Id}] Attacking Ship {target.Id} of client {target.OwningEscadreClientId} (Escadre Entity: {target.OwningEscadre.Id}).");
            DamageInfo damage = new DamageInfo(
                AttackDamage,
                DamageType.Kinetic,
                target.Position, 
                (target.Position - Position).Normalized, 
                this.Id,
                this.OwningEscadreClientId
            );
            target.ApplyDamage(damage);
        }
        
        // Override Entity.Death() for specific Ship death behavior
        protected override void Death()
        {
            base.Death(); // Call base DestructibleEntity.Death

            // Notify the owning escadre that this ship has been destroyed
            if (OwningEscadre != null && !OwningEscadre.IsDead) // Check if escadre still exists
            {
                OwningEscadre.HandleShipDestroyed(this.Id);
            }
            Logger.Log($"[Ship {Id}] Ship Death() processed. Notified Escadre {OwningEscadre?.Id}.");
        }

        public virtual void PerformUpgrade()
        {
            Logger.Log($"[Ship {Id}] Base PerformUpgrade called. No changes by default.");
            // Derived classes will implement specific stat changes.
        }

        protected override void ObligatoryOnRemove()
        {
            base.ObligatoryOnRemove();
            OnMovementTargetProgrammed = null; // Clear events
            // The OwningEscadre will handle removing this ship from its lists when the ship dies.
        }
    }
}