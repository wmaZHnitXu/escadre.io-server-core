// File: Scripts/Server/Core/Model/DefaultShip.cs
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    public class DefaultShip : Ship
    {
        public override EntityTypeEnum EntityType => EntityTypeEnum.DefaultShip;

        // Implement abstract properties from Ship
        public override float MaxSpeed { get; protected set; }
        public override float TurnRate { get; protected set; }

        public DefaultShip(Level level, Escadre ownerEscadre, Vector3 initialPosition)
            : base(level, ownerEscadre, initialPosition, 75f) // Base MaxHealth for DefaultShip
        {
            // Initialize stats for this specific ship type
            MaxSpeed = 4f;
            TurnRate = 75f;

            // Add cannons
            // Cannon 1 (Front-Left)
            var cannon1 = new DefaultCannon(
                level: _level, 
                ownerShip: this,
                localPositionOffset: new Vector3(-0.3f, 0.1f, 0.7f),
                localRotationOffset: Quaternion.Identity,
                attackRange: 18f,
                attackDamage: 5f,
                attackCooldown: 2.0f
                // ProjectileSpawnOffset will use default from DefaultCannon constructor
            );
            _cannons.Add(cannon1);

            // Cannon 2 (Front-Right)
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

            Logger.Log($"[DefaultShip ID pending:{this.Id}] Created for Escadre {ownerEscadre.OwnerClientId}. HP: {CurrentHealth}/{MaxHealth}. Added {Cannons.Count} cannons.");
        }

        public override void PerformUpgrade()
        {
            base.PerformUpgrade(); // Calls the base Ship method for logging

            MaxHealth += 25;
            CurrentHealth = MaxHealth; // Heal to new max
            MaxSpeed += 0.5f;
            TurnRate += 10f;

            // Upgrade cannons
            foreach (var cannon in _cannons)
            {
                // Example upgrade: Increase damage and range, reduce cooldown slightly
                // Note: This directly modifies protected setters. Consider dedicated upgrade methods on Cannon if more complex.
                // For now, let's assume DefaultCannon properties can be modified if it had public setters or an UpgradeCannon method.
                // Since AttackDamage etc. on DefaultCannon have protected setters, we can't directly modify them here.
                // To make them upgradable, DefaultCannon would need an Upgrade() method or public setters for relevant stats.
                // For this example, let's imagine DefaultCannon has an UpgradeCannon() method.
                // cannon.UpgradeCannon(damageBoost: 2f, rangeBoost: 2f, cooldownReduction: 0.2f);
            }

            Logger.Log($"[DefaultShip {Id}] Upgraded! New Stats -> HP: {MaxHealth}, Spd: {MaxSpeed}, Turn: {TurnRate}. Cannons may also be upgraded.");
        }
    }
}