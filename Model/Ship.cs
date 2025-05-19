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

        public abstract float MaxSpeed { get; protected set; }
        public abstract float TurnRate { get; protected set; }
        public abstract float AttackDamage { get; protected set; }
        public abstract float AttackRange { get; protected set; }
        public abstract float AttackCooldown { get; protected set; }

        public int OwningEscadreClientId => _owningEscadre.OwnerClientId;
        public float CurrentSpeed { get; protected set; }
        protected float _currentAttackCooldown = 0f;
        protected Vector2? _movementTargetPosition;

        /// <summary>
        /// Event raised when the ship's internal movement target is set.
        /// Provides the ship instance and its new target.
        /// The float parameter is for the current server time when this was set.
        /// </summary>
        public event Action<Ship, Vector2?, float /*serverTime*/> OnMovementTargetProgrammed;


        public Ship(Level level, Escadre ownerEscadre, Vector3 initialPosition, float maxHealth)
            : base(level, maxHealth)
        {
            _owningEscadre = ownerEscadre ?? throw new ArgumentNullException(nameof(ownerEscadre));
            Position = initialPosition;
        }

        public override void Update(float delta)
        {
            base.Update(delta);
            if (IsDead) return;
            UpdateMovement(delta);
            UpdateAttack(delta);
            if (_currentAttackCooldown > 0) _currentAttackCooldown -= delta;
        }

        public void SetMovementTarget(Vector2? target, float serverTime) // Added serverTime
        {
            if (_movementTargetPosition == target && target.HasValue == _movementTargetPosition.HasValue) return; // No change
            _movementTargetPosition = target;
            OnMovementTargetProgrammed?.Invoke(this, _movementTargetPosition, serverTime);
        }
        // ... (DebugGetMovementTarget, PerformUpgrade, UpdateMovement, UpdateAttack, ApplyDamage, Death as before) ...
        public bool DebugGetMovementTarget(out Vector2 target) { if (_movementTargetPosition.HasValue) { target = _movementTargetPosition.Value; return true; } target = Vector2.Zero; return false; }
        public virtual void PerformUpgrade() { }
        protected virtual void UpdateMovement(float delta) { /* ... as before ... */
            if (!_movementTargetPosition.HasValue) { CurrentSpeed = 0f; return; }
            Vector2 currentPos2D = new Vector2(Position.X, Position.Z); Vector2 targetPos2D = _movementTargetPosition.Value;
            Vector2 toTarget = targetPos2D - currentPos2D;
            if (toTarget.SqrMagnitude < 0.01f * 0.01f) { _movementTargetPosition = null; CurrentSpeed = 0f; Position = new Vector3(targetPos2D.X, Position.Y, targetPos2D.Y); return; }
            Vector2 directionToTarget = toTarget.Normalized; CurrentSpeed = MaxSpeed;
            Vector3 targetForward = new Vector3(directionToTarget.X, 0, directionToTarget.Y);
            if (targetForward.SqrMagnitude > Vector3.Epsilon) { Quaternion targetRotation = Quaternion.LookRotation(targetForward, Vector3.Up); Rotation = Quaternion.RotateTowards(Rotation, targetRotation, TurnRate * delta); }
            Vector3 velocity = Rotation * Vector3.Forward * CurrentSpeed * delta; Position += velocity;
        }
        protected virtual void UpdateAttack(float delta) { /* ... as before ... */
             if (_owningEscadre.TargetEscadreOwnerClientIds.Count == 0 || _currentAttackCooldown > 0f) { return; }
            DestructibleEntity bestTarget = null; float minSqrDistance = float.MaxValue; float attackRangeSqr = AttackRange * AttackRange;
            foreach (int targetEscadreOwnerId in _owningEscadre.TargetEscadreOwnerClientIds) {
                if (_level.TryGetEscadre(targetEscadreOwnerId, out Escadre targetEscadre)) {
                    foreach (int enemyShipId in targetEscadre.ShipEntityIds) {
                        if (_level.TryGetEntity(enemyShipId, out Entity entity) && entity is DestructibleEntity enemyShip) {
                            if (enemyShip.IsDead || enemyShip == this) continue;
                            float sqrDist = (enemyShip.Position - Position).SqrMagnitude;
                            if (sqrDist <= attackRangeSqr && sqrDist < minSqrDistance) { minSqrDistance = sqrDist; bestTarget = enemyShip; }
                        } } } }
            if (bestTarget != null) {
                Vector3 directionToBestTarget = (bestTarget.Position - Position).Normalized;
                bestTarget.ApplyDamage(new DamageInfo(AttackDamage, DamageType.Kinetic, bestTarget.Position, directionToBestTarget, Id, OwningEscadreClientId));
                _currentAttackCooldown = AttackCooldown; //Logger.Log($"[Ship {Id}] Attacking Ship {bestTarget.Id}!");
            }
        }
        public override void ApplyDamage(DamageInfo damageInfo) { base.ApplyDamage(damageInfo); }
        protected override void Death() { base.Death(); Logger.Log($"[Ship {Id}] BOOM! Ship destroyed."); _owningEscadre.HandleShipDestroyed(this.Id); }
    }
}