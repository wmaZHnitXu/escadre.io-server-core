// File: Scripts/Server/Core/Model/Ship.cs
using System;
using Core.Primitives;
using Core.Logging;
using System.Linq;
using System.Collections.Generic;

namespace Core.Model
{
    public abstract class Ship : DestructibleEntity
    {
        protected readonly Escadre _owningEscadre;

        // --- Abstract Stats to be defined by concrete ship classes ---
        public abstract float MaxSpeed { get; protected set; }
        public abstract float TurnRate { get; protected set; } // Degrees per second
        public abstract float AttackDamage { get; protected set; }
        public abstract float AttackRange { get; protected set; }
        public abstract float AttackCooldown { get; protected set; }

        // --- Properties with shared logic ---
        public int OwningEscadreClientId => _owningEscadre.OwnerClientId;
        public float CurrentSpeed { get; protected set; }
        protected float _currentAttackCooldown = 0f;
        protected Vector2? _movementTargetPosition;

        public Ship(Level level, Escadre ownerEscadre, Vector3 initialPosition, float maxHealth) // MaxHealth is passed from concrete constructor
            : base(level, maxHealth)
        {
            _owningEscadre = ownerEscadre ?? throw new ArgumentNullException(nameof(ownerEscadre));
            Position = initialPosition;
            // Concrete ship constructors will set MaxHealth (passed to base) and the abstract stats.
        }

        public override void Update(float delta)
        {
            base.Update(delta); // Handles OnDamaged event if base class needs to
            if (IsDead) return;

            UpdateMovement(delta);
            UpdateAttack(delta);

            if (_currentAttackCooldown > 0)
            {
                _currentAttackCooldown -= delta;
            }
        }

        public void SetMovementTarget(Vector2? target)
        {
            _movementTargetPosition = target;
        }

        /// <summary>
        /// Placeholder for upgrade logic. Resource checking and deduction
        /// will be handled by an external system or Escadre.
        /// This method should only apply the stat changes to the ship.
        /// </summary>
        public virtual void PerformUpgrade()
        {
            // Logger.Log($"[Ship {Id}] PerformUpgrade called. (Base implementation is empty)");
            // Concrete ships will override this to change their abstract stats
            // e.g., MaxSpeed += 1f; MaxHealth += 20f; CurrentHealth = MaxHealth;
            // This might also trigger a specific "UpgradedEvent" for network proxies if needed.
        }

        protected virtual void UpdateMovement(float delta)
        {
            if (!_movementTargetPosition.HasValue)
            {
                CurrentSpeed = 0f;
                return;
            }

            Vector2 currentPos2D = new Vector2(Position.X, Position.Z);
            Vector2 targetPos2D = _movementTargetPosition.Value;
            Vector2 toTarget = targetPos2D - currentPos2D;

            if (toTarget.SqrMagnitude < 0.1f * 0.1f)
            {
                _movementTargetPosition = null;
                CurrentSpeed = 0f;
                Position = new Vector3(targetPos2D.X, Position.Y, targetPos2D.Y);
                return;
            }

            Vector2 directionToTarget = toTarget.Normalized;
            CurrentSpeed = MaxSpeed; // Assumes full speed towards target

            Vector3 targetForward = new Vector3(directionToTarget.X, 0, directionToTarget.Y);
            if (targetForward.SqrMagnitude > Primitives.Vector3.Epsilon)
            {
                Quaternion targetRotation = Primitives.Quaternion.LookRotation(targetForward, Primitives.Vector3.Up);
                Rotation = Primitives.Quaternion.RotateTowards(Rotation, targetRotation, TurnRate * delta);
            }

            // Move based on current rotation and speed
            Vector3 velocity = Rotation * Primitives.Vector3.Forward * CurrentSpeed * delta;
            Position += velocity;
        }

        protected virtual void UpdateAttack(float delta)
        {
            if (_owningEscadre.TargetEscadreOwnerClientIds.Count == 0 || _currentAttackCooldown > 0f)
            {
                return;
            }

            DestructibleEntity bestTarget = null;
            float minSqrDistance = float.MaxValue;
            float attackRangeSqr = AttackRange * AttackRange;

            foreach (int targetEscadreOwnerId in _owningEscadre.TargetEscadreOwnerClientIds)
            {
                if (_level.TryGetEscadre(targetEscadreOwnerId, out Escadre targetEscadre))
                {
                    foreach (int enemyShipId in targetEscadre.ShipEntityIds)
                    {
                        if (_level.TryGetEntity(enemyShipId, out Entity entity) && entity is DestructibleEntity enemyShip)
                        {
                            if (enemyShip.IsDead || enemyShip == this) continue;

                            float sqrDist = (enemyShip.Position - Position).SqrMagnitude;
                            if (sqrDist <= attackRangeSqr && sqrDist < minSqrDistance)
                            {
                                minSqrDistance = sqrDist;
                                bestTarget = enemyShip;
                            }
                        }
                    }
                }
            }

            if (bestTarget != null)
            {
                Vector3 directionToBestTarget = (bestTarget.Position - Position).Normalized;
                // Consider adding a check: is ship facing the target within a certain fire arc?
                // float angleToTarget = Vector3.Angle(Rotation * Vector3.Forward, directionToBestTarget);
                // if (angleToTarget < Config.ShipFiringArcDegrees) { ... }

                Logger.Log($"[Ship {Id}] Attacking target Ship {bestTarget.Id}!");
                bestTarget.ApplyDamage(new DamageInfo(AttackDamage, DamageType.Kinetic, bestTarget.Position, directionToBestTarget, Id, OwningEscadreClientId));
                _currentAttackCooldown = AttackCooldown;
            }
        }

        public override void ApplyDamage(DamageInfo damageInfo)
        {
            base.ApplyDamage(damageInfo);
        }

        protected override void Death()
        {
            base.Death();
            Logger.Log($"[Ship {Id}] BOOM! Ship destroyed.");
            _owningEscadre.HandleShipDestroyed(this.Id);
        }
    }
}