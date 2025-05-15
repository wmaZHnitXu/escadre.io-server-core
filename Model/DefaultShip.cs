// File: Scripts/Server/Core/Model/DefaultShip.cs
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    public class DefaultShip : Ship
    {
        public override EntityTypeEnum EntityType => EntityTypeEnum.DefaultShip;

        // Implement abstract properties
        public override float MaxSpeed { get; protected set; }
        public override float TurnRate { get; protected set; }
        public override float AttackDamage { get; protected set; }
        public override float AttackRange { get; protected set; }
        public override float AttackCooldown { get; protected set; }

        public DefaultShip(Level level, Escadre ownerEscadre, Vector3 initialPosition)
            : base(level, ownerEscadre, initialPosition, 75f) // Base MaxHealth for DefaultShip
        {
            // Initialize stats for this specific ship type
            MaxSpeed = 4f;
            TurnRate = 75f;
            AttackDamage = 8f;
            AttackRange = 18f;
            AttackCooldown = 2.5f;
            Logger.Log($"[DefaultShip ID pending:{this.Id}] Created for Escadre {ownerEscadre.OwnerClientId}. HP: {CurrentHealth}/{MaxHealth}");
        }

        public override void PerformUpgrade()
        {
            // This is where the ship would change its own stats.
            // Resource checking/deduction is external.
            // For now, let's assume an upgrade always succeeds if called.
            base.PerformUpgrade(); // Calls the empty base method

            MaxHealth += 25;
            CurrentHealth = MaxHealth; // Heal to new max
            AttackDamage += 2;
            MaxSpeed += 0.5f;
            AttackRange += 2f;
            TurnRate += 10f;

            Logger.Log($"[DefaultShip {Id}] Upgraded! New Stats -> HP: {MaxHealth}, Dmg: {AttackDamage}, Spd: {MaxSpeed}, Rng: {AttackRange}, Turn: {TurnRate}");
            // Potentially raise an event that this ship was upgraded, so proxies can send new full stat block or specific update.
            // OnUpgraded?.Invoke(this);
        }
    }
}