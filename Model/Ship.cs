// File: Core/Model/Ship.cs
using System;
using Core.Primitives;
using Core.Logging;
using System.Linq;

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
            OwningEscadreClientId = ownerEscadre.OwnerClientId;
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
                IsMoving = true; // External command to move
            }
            else
            {
                // If target is cleared externally, IsMoving might be set false here or let UpdateMovement decide.
                // For now, clearing the target implies the *intent* to stop or change behavior.
                // UpdateMovement will handle actual stopping.
                 IsMoving = false; // Explicitly stop if target is cleared
                 CurrentSpeed = 0f; // Snap speed to 0 if command is to stop
            }

            if (changed || targetWorldPosition.HasValue) // Fire event if target changes or is (re)set
            {
                OnMovementTargetProgrammed?.Invoke(this, _movementTargetPosition, serverTime);
            }
        }


        protected virtual void UpdateMovement(float deltaTime)
        {
            // If not externally ordered to move via SetMovementTarget (which sets IsMoving)
            // or if no target position is set, decelerate and stop.
            if (!IsMoving || !_movementTargetPosition.HasValue)
            {
                if (CurrentSpeed > 0)
                {
                    CurrentSpeed = Math.Max(0, CurrentSpeed - (MaxSpeed * 2f * deltaTime)); 
                }
                else { CurrentSpeed = 0f; }
                IsMoving = false; // Ensure IsMoving is false if we are stopping/stopped.
                return;
            }

            Vector2 currentPos2D = new Vector2(Position.X, Position.Z);
            Vector2 targetPos2D = _movementTargetPosition.Value;
            Vector2 toTarget = targetPos2D - currentPos2D;

            float distanceToTargetSq = toTarget.SqrMagnitude;
            
            // Server's stopping condition based on its own deltaTime and MaxSpeed
            float stoppingDistance = MaxSpeed * deltaTime * 0.5f; // How far it *will* move this frame
            float stoppingDistanceSq = stoppingDistance * stoppingDistance;
            // Ensure a minimum practical threshold to prevent endless tiny movements or division issues
            stoppingDistanceSq = Math.Max(0.0025f, stoppingDistanceSq); // e.g., 0.05 * 0.05

            if (distanceToTargetSq < stoppingDistanceSq)
            {
                IsMoving = false; 
                CurrentSpeed = 0f;
                // Snap to target XZ to ensure it truly arrives for game logic
                Position = new Vector3(targetPos2D.X, Position.Y, targetPos2D.Y); 
                // Logger.Log($"[Ship {Id} SERVER] Reached movement target {targetPos2D}.");
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

            if (IsDead || _currentAttackCooldownTimer > 0) return;

            if (!OwningEscadre.TargetEscadreOwnerClientIds.Any()) return;

            Ship targetShip = FindBestTarget();

            if (targetShip != null)
            {
                PerformAttackOn(targetShip);
                _currentAttackCooldownTimer = AttackCooldown;
            }
        }

        protected Ship FindBestTarget()
        {
            Ship bestTarget = null;
            float closestDistSq = AttackRange * AttackRange;

            foreach (int targetOwnerId in OwningEscadre.TargetEscadreOwnerClientIds)
            {
                if (_level.TryGetEscadre(targetOwnerId, out Escadre targetEscadre))
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
            }
            return bestTarget;
        }

        protected virtual void PerformAttackOn(Ship target)
        {
            Logger.Log($"[Ship {Id}] Attacking Ship {target.Id} of client {target.OwningEscadreClientId}.");
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
        
        public virtual void PerformUpgrade()
        {
            Logger.Log($"[Ship {Id}] Base PerformUpgrade called. No changes by default.");
        }
    }
}