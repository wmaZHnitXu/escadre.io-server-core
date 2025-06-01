// File: Core/Model/Bullet.cs
using Core.Primitives;

namespace Core.Model
{
    public class Bullet : Projectile
    {
        public override EntityTypeEnum EntityType => EntityTypeEnum.Bullet;
        public override float BoundingRadius2D { get; protected set; } = 0.1f; // Bullets are small

        public Bullet(
            Level level,
            int ownerEntityId,         // ID of the Cannon that fired it
            int ownerClientId,         // Client ID of the Escadre owning the Cannon's Ship
            Vector3 initialPosition,
            Vector3 initialVelocity,   // Should be fairly high speed
            DamageInfo damagePayload,
            float maxLifetime = 2.0f,  // e.g., 2 seconds lifetime
            float serverTimeOfSpawn = 0f) // Pass current server time
            : base(level, ownerEntityId, ownerClientId, initialPosition, initialVelocity, damagePayload, maxLifetime, serverTimeOfSpawn)
        {
            // Specific Bullet initialization if any
        }

        // No FloatingBehavior for bullets by default
    }
}