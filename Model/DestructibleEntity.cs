// File: Scripts/Server/Core/Model/DestructibleEntity.cs
using System;
using Core.Logging;

namespace Core.Model
{
    public abstract class DestructibleEntity : Entity
    {
        public float MaxHealth { get; protected set; }
        public float CurrentHealth { get; protected set; }

        /// <summary>
        /// Event raised when this entity takes damage.
        /// Parameters: DamageInfo, DestructibleEntity (self)
        /// </summary>
        public event Action<DamageInfo, DestructibleEntity> OnDamaged;

        protected DestructibleEntity(Level level, float maxHealth) : base(level)
        {
            if (maxHealth <= 0) throw new ArgumentOutOfRangeException(nameof(maxHealth), "MaxHealth must be positive.");
            MaxHealth = maxHealth;
            CurrentHealth = maxHealth;
        }

        /// <summary>
        /// Applies damage to this entity.
        /// </summary>
        /// <param name="damageInfo">Information about the damage being applied.</param>
        public virtual void ApplyDamage(DamageInfo damageInfo)
        {
            if (IsDead || damageInfo.Amount <= 0) return;

            // Apply resistances/vulnerabilities based on damageInfo.Type here if needed
            float actualDamage = damageInfo.Amount;

            CurrentHealth -= actualDamage;
            Logger.Log($"[DestructibleEntity {Id}] Took {actualDamage} {damageInfo.Type} damage. HP: {CurrentHealth}/{MaxHealth}");

            OnDamaged?.Invoke(damageInfo, this);

            if (CurrentHealth <= 0)
            {
                CurrentHealth = 0;
                // Kill is called without silent=true by default, triggering Death() and OnDestructionEvent
                Kill(false);
            }
        }

        /// <summary>
        /// Heals the entity for a given amount.
        /// </summary>
        public virtual void Heal(float amount)
        {
            if (IsDead || amount <= 0) return;
            CurrentHealth = Math.Min(CurrentHealth + amount, MaxHealth);
             Logger.Log($"[DestructibleEntity {Id}] Healed for {amount}. HP: {CurrentHealth}/{MaxHealth}");
        }
    }
}