using System;
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    public class DefaultCannon : Sentry<Ship>
    {
        public override EntityTypeEnum EntityType => EntityTypeEnum.DefaultCannon;

        // Implement the abstract property from Sentry
        public override float AttackRange { get; protected set; }

        public float AttackDamage { get; protected set; }
        public float AttackCooldown { get; protected set; }
        protected float _currentAttackCooldownTimer;
        public Vector3 ProjectileSpawnOffset { get; protected set; }
        public float ProjectileSpeed { get; protected set; } = 50f; // Speed of the bullet

        // Aiming tolerance: dot product threshold (e.g., cos(5 degrees) = 0.996)
        private const float AIM_ACCURACY_DOT_THRESHOLD = 0.996f; 

        public DefaultCannon(
            Level level,
            Ship ownerShip,
            Vector3 localPositionOffset,      // Attachment point on the ship
            Quaternion localRotationOffset,   // Initial orientation of the cannon model relative to attachment point
            float attackRange,                // This will set the overridden AttackRange property
            float attackDamage,
            float attackCooldown,
            Vector3? projectileSpawnOffset = null) 
            : base(level, ownerShip, localPositionOffset, localRotationOffset, attackRange) // Pass attackRange to base for its use if needed, though Sentry doesn't directly use it in constructor beyond what derived class does.
        {
            this.AttackRange = attackRange; // Set the implemented abstract property
            AttackDamage = attackDamage;
            AttackCooldown = attackCooldown;
            _currentAttackCooldownTimer = 0f; // Start ready to fire, or set to AttackCooldown for initial delay
            ProjectileSpawnOffset = projectileSpawnOffset ?? (Vector3.Forward * 0.5f); // Default if not provided

            // Logger.Log($"[DefaultCannon ID pending:{this.Id}] Created for Ship {ownerShip.Id}. Range: {this.AttackRange}, Dmg: {AttackDamage}, CD: {AttackCooldown}");
        }

        public override void Update(float delta)
        {
            base.Update(delta); // Handles Sentry logic: target acquisition and aiming

            if (IsDead || Owner == null || Owner.IsDead)
            {
                return;
            }

            if (_currentAttackCooldownTimer > 0)
            {
                _currentAttackCooldownTimer -= delta;
            }

            if (_currentTarget != null && _currentAttackCooldownTimer <= 0)
            {
                // Sentry class (_base) ensures _currentTarget is valid, in range, and in firing arc before AimAtTarget is effective.
                // We just need to check if we are sufficiently aimed at it now.
                TryShootAt(_currentTarget);
            }
        }

        protected virtual void TryShootAt(Entity target)
        {
            if (target == null || target.IsDead || !(target is DestructibleEntity destructibleTarget))
            {
                // Logger.LogWarning($"[DefaultCannon {Id}] Cannot shoot at invalid target {target?.Id}");
                return;
            }

            // Check if cannon is reasonably aimed at the target
            Vector3 directionToTarget = (target.Position - this.Position).Normalized;
            Vector3 cannonForward = this.Rotation * Vector3.Forward; // Current facing direction of the cannon

            if (Vector3.Dot(cannonForward, directionToTarget) < AIM_ACCURACY_DOT_THRESHOLD)
            {
                // Not aimed well enough yet
                // Logger.Log($"[DefaultCannon {Id}] Waiting for better aim on target {target.Id}. Dot: {Vector3.Dot(cannonForward, directionToTarget)}");
                return;
            }

            // --- Perform the shot --- 
            Vector3 projectileSpawnWorldPosition = this.Position + (this.Rotation * ProjectileSpawnOffset);
            Vector3 projectileVelocity = cannonForward * ProjectileSpeed;

            DamageInfo damageInfoPayload = new DamageInfo(
                AttackDamage,
                DamageType.Kinetic, 
                projectileSpawnWorldPosition, // This will be overwritten by bullet if it hits, but good for origin
                cannonForward, 
                this.Id, 
                this.Owner.OwningEscadreClientId 
            );

            // Create and launch the bullet
            Bullet bullet = new Bullet(
                _level,
                this.Id,                        // Owner of the projectile is the cannon
                this.Owner.OwningEscadreClientId, // Client owner from the ship
                projectileSpawnWorldPosition,
                projectileVelocity,
                damageInfoPayload,
                this.AttackRange / ProjectileSpeed + 0.5f, // Max lifetime based on range and speed + buffer
                _level.CurrentTime                  // Server time of spawn
            );
            // The bullet's constructor calls _level.AddEntity(bullet);

            Logger.Log($"[DefaultCannon {Id} on Ship {Owner.Id}] Fired Bullet {bullet.Id} at {target.GetType().Name} {target.Id}.");
            // Damage is applied by the bullet on impact, not directly by the cannon here.
            // destructibleTarget.ApplyDamage(damageInfo); // This was removed

            _currentAttackCooldownTimer = AttackCooldown;
        }
    }
}