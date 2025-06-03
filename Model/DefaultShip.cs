// File: Scripts/Server/Core/Model/DefaultShip.cs
using Core.Primitives;
using Core.Logging;
using Core.Ocean;
using System.Collections.Generic;
using System; // For MathF

namespace Core.Model
{
    public class DefaultShip : Ship
    {
        public override EntityTypeEnum EntityType => EntityTypeEnum.DefaultShip;

        public override float MaxSpeed { get; protected set; }
        public override float TurnRate { get; protected set; }
        public override float BoundingRadius2D { get; protected set; } = 2.5f; // Override for specific ship size

        public DefaultShip(Level level, Escadre ownerEscadre, Vector3 initialPosition)
            : base(level, ownerEscadre, initialPosition, maxHealth:75f) 
        {
            MaxSpeed = 4.0f;
            TurnRate = 75f; // Degrees per second
            CollectableDetectionRange = 8.0f; 

            // Initialize movement parameters from Ship base class
            AccelerationRate = MaxSpeed / 2.0f; 
            DecelerationRate = MaxSpeed / 1.0f; 

            // StoppingDistance: distance needed to stop from MaxSpeed with full deceleration (DecelerationRate * 2f)
            // d = v^2 / (2*a), where a = DecelerationRate * 2f
            // So, d = MaxSpeed^2 / (4 * DecelerationRate).
            // If DecelerationRate = MaxSpeed, then d = MaxSpeed / 4.
            StoppingDistance = (MaxSpeed / 4.0f) + 0.1f; // Added small buffer
            SlowingDistance = StoppingDistance * 3.0f; // Start slowing down much earlier
            FormationThreshold = 0.5f;        

            var floatingPoints = new List<Vector3>
            {
                new Vector3(0f, 0f, 2.0f),   
                new Vector3(0f, 0f, -2.0f),  
                new Vector3(0.5f, 0f, 0f),   
                new Vector3(-0.5f, 0f, 0f)   
            };

            if (level.OceanDataProvider != null)
            {
                this.FloatingBehavior = new MultiPointFloatingBehavior(floatingPoints, level.OceanDataProvider);
            }
            else 
            {
                Logger.LogWarning($"[DefaultShip ID:{this.Id}] OceanDataProvider not available in Level. FloatingBehavior not initialized.");
            }

            AddCollider(new BoxCollider(this, new Vector3(0, 0.25f, 0f), new Vector3(1.5f, 1.2f, 4.5f)));


            var cannon1 = new DefaultCannon(
                level: _level, 
                ownerShip: this,
                localPositionOffset: new Vector3(-0.3f, 0.1f, 0.7f),
                localRotationOffset: Quaternion.Identity,
                attackRange: 36f,
                attackDamage: 5f,
                attackCooldown: 2.0f
            );
            _cannons.Add(cannon1);

            var cannon2 = new DefaultCannon(
                level: _level,
                ownerShip: this,
                localPositionOffset: new Vector3(0.3f, 0.1f, 0.7f),
                localRotationOffset: Quaternion.Identity,
                attackRange: 36f,
                attackDamage: 5f,
                attackCooldown: 2.0f
            );
            _cannons.Add(cannon2);
        }

        public override void PerformUpgrade()
        {
            base.PerformUpgrade(); 

            MaxHealth += 25;
            CurrentHealth = MaxHealth; 
            MaxSpeed += 0.5f;
            TurnRate += 10f;
            CollectableDetectionRange += 1.0f; 
            BoundingRadius2D += 0.2f; 

            AccelerationRate = MaxSpeed / 2.0f; 
            DecelerationRate = MaxSpeed / 1.0f;
            StoppingDistance = (MaxSpeed / 4.0f) + 0.1f; 
            SlowingDistance = StoppingDistance * 3.0f;

            Logger.Log($"[DefaultShip {Id}] Upgraded! New Stats -> HP: {MaxHealth}, Spd: {MaxSpeed}, Turn: {TurnRate}, CollectRange: {CollectableDetectionRange}, BoundsR: {BoundingRadius2D}.");
            Logger.Log($"[DefaultShip {Id}] Upgraded Movement -> SlowDist: {SlowingDistance}, StopDist: {StoppingDistance}, Accel: {AccelerationRate}");
        }
    }
}