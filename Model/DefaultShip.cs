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

        // Implement abstract properties from Ship
        public override float MaxSpeed { get; protected set; }
        public override float TurnRate { get; protected set; }

        public DefaultShip(Level level, Escadre ownerEscadre, Vector3 initialPosition)
            : base(level, ownerEscadre, initialPosition, 75f) // Base MaxHealth for DefaultShip
        {
            // Initialize stats for this specific ship type
            MaxSpeed = 4f;
            TurnRate = 75f;

            // Define floating points for this ship model (local offsets)
            // Example: A simple box shape with 4 points: bow, stern, port, starboard
            // Assuming ship length is ~2 units (Z), width ~1 unit (X)
            var floatingPoints = new List<Vector3>
            {
                new Vector3(0f, 0f, 1.0f),   // Bow
                new Vector3(0f, 0f, -1.0f),  // Stern
                new Vector3(0.5f, 0f, 0f),   // Starboard mid
                new Vector3(-0.5f, 0f, 0f)   // Port mid
            };

            // If OceanDataProvider is available in the level, create the behavior
            if (level.OceanDataProvider != null)
            {
                this.FloatingBehavior = new MultiPointFloatingBehavior(floatingPoints, level.OceanDataProvider);
            }
            else // Fallback or if ocean is disabled server-side
            {
                // Optionally, log a warning or use a null/dummy behavior
                // this.FloatingBehavior = null; 
                Logger.LogWarning($"[DefaultShip ID:{this.Id}] OceanDataProvider not available in Level. FloatingBehavior not initialized.");
            }


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

            // Logger.Log($"[DefaultShip ID pending:{this.Id}] Created for Escadre {ownerEscadre.OwnerClientId}. HP: {CurrentHealth}/{MaxHealth}. Added {Cannons.Count} cannons.");
        }

        public override void PerformUpgrade()
        {
            base.PerformUpgrade(); // Calls the base Ship method for logging

            MaxHealth += 25;
            CurrentHealth = MaxHealth; // Heal to new max
            MaxSpeed += 0.5f;
            TurnRate += 10f;

            // Cannons upgrade logic would go here if DefaultCannon had an Upgrade() method
            // foreach (var cannon in _cannons) { cannon.Upgrade(); }

            Logger.Log($"[DefaultShip {Id}] Upgraded! New Stats -> HP: {MaxHealth}, Spd: {MaxSpeed}, Turn: {TurnRate}. Cannons may also be upgraded.");
        }
    }
}