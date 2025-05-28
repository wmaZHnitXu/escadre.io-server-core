// File: Scripts/Server/Core/Model/DefaultShip.cs
using Core.Primitives;
using Core.Logging;
using Core.Ocean;
using System.Collections.Generic;

namespace Core.Model
{
    public class DefaultShip : Ship
    {
        public override EntityTypeEnum EntityType => EntityTypeEnum.DefaultShip;

        public override float MaxSpeed { get; protected set; }
        public override float TurnRate { get; protected set; }
        public override float BoundingRadius2D { get; protected set; } = 2.5f; // Override for specific ship size

        public DefaultShip(Level level, Escadre ownerEscadre, Vector3 initialPosition)
            : base(level, ownerEscadre, initialPosition, 75f) 
        {
            MaxSpeed = 4f;
            TurnRate = 75f;
            CollectableDetectionRange = 8.0f; 

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

            var cannon1 = new DefaultCannon(
                level: _level, 
                ownerShip: this,
                localPositionOffset: new Vector3(-0.3f, 0.1f, 0.7f),
                localRotationOffset: Quaternion.Identity,
                attackRange: 18f,
                attackDamage: 5f,
                attackCooldown: 2.0f
            );
            _cannons.Add(cannon1);

            var cannon2 = new DefaultCannon(
                level: _level,
                ownerShip: this,
                localPositionOffset: new Vector3(0.3f, 0.1f, 0.7f),
                localRotationOffset: Quaternion.Identity,
                attackRange: 18f,
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
            BoundingRadius2D += 0.2f; // Example: slightly larger visually/for coarse collision

            Logger.Log($"[DefaultShip {Id}] Upgraded! New Stats -> HP: {MaxHealth}, Spd: {MaxSpeed}, Turn: {TurnRate}, CollectRange: {CollectableDetectionRange}, BoundsR: {BoundingRadius2D}.");
        }
    }
}