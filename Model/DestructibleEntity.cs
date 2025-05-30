// File: Scripts/Server/Core/Model/DestructibleEntity.cs
using System;
using System.Collections.Generic; // Required for List
using System.Linq; // Required for OrderBy
using Core.Logging;
using Core.Primitives; // Required for Collider

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

        protected readonly List<Collider> _colliders = new List<Collider>();
        public IReadOnlyList<Collider> Colliders => _colliders.AsReadOnly();


        protected DestructibleEntity(Level level, float maxHealth) : base(level)
        {
            if (maxHealth <= 0) throw new ArgumentOutOfRangeException(nameof(maxHealth), "MaxHealth must be positive.");
            MaxHealth = maxHealth;
            CurrentHealth = maxHealth;
        }

        public virtual void AddCollider(Collider collider)
        {
            if (collider == null) throw new ArgumentNullException(nameof(collider));
            if (collider.OwnerEntity != this)
            {
                // This check ensures the collider was constructed with this entity as owner,
                // or if we want to allow re-parenting, we'd set it here.
                // For now, assume constructor sets it.
                // If collider.OwnerEntity is null, we can set it:
                if(collider.OwnerEntity == null) collider.OwnerEntity = this;
                else throw new ArgumentException("Collider is already owned by another entity or owner mismatch.", nameof(collider));
            }
            if (!_colliders.Contains(collider))
            {
                _colliders.Add(collider);
            }
        }

        public virtual void RemoveCollider(Collider collider)
        {
            if (collider == null) return;
            if (_colliders.Remove(collider))
            {
                collider.OwnerEntity = null; // Orphan the collider
            }
        }

        public virtual void ClearColliders()
        {
            foreach(var collider in _colliders)
            {
                collider.OwnerEntity = null;
            }
            _colliders.Clear();
        }
        
        /// <summary>
        /// Checks if a ray intersects any of this entity's colliders.
        /// </summary>
        /// <param name="worldRayOrigin">The origin of the ray in world space.</param>
        /// <param name="worldRayDirection">The direction of the ray in world space (should be normalized).</param>
        /// <param name="maxDistance">The maximum distance to check for intersections.</param>
        /// <param name="hitCollider">Output: The specific collider that was hit, if any.</param>
        /// <param name="hitDistance">Output: The distance to the closest intersection point.</param>
        /// <param name="hitPoint">Output: The closest intersection point in world space.</param>
        /// <param name="hitNormal">Output: The normal at the closest intersection point in world space.</param>
        /// <returns>True if an intersection occurs, false otherwise.</returns>
        public virtual bool CheckRayIntersection(
            Vector3 worldRayOrigin, 
            Vector3 worldRayDirection, 
            float maxDistance,
            out Collider hitCollider,
            out float hitDistance, 
            out Vector3 hitPoint, 
            out Vector3 hitNormal)
        {
            hitCollider = null;
            hitDistance = float.MaxValue;
            hitPoint = Vector3.Zero;
            hitNormal = Vector3.Zero;

            if (IsDead) return false;

            bool foundHit = false;
            foreach (var collider in _colliders)
            {
                if (collider.IntersectsRay(worldRayOrigin, worldRayDirection, Math.Min(maxDistance, hitDistance), 
                                           out float currentDist, out Vector3 currentPoint, out Vector3 currentNormal))
                {
                    if (currentDist < hitDistance)
                    {
                        foundHit = true;
                        hitDistance = currentDist;
                        hitPoint = currentPoint;
                        hitNormal = currentNormal;
                        hitCollider = collider;
                    }
                }
            }
            return foundHit;
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

        protected override void ObligatoryOnRemove()
        {
            base.ObligatoryOnRemove();
            ClearColliders();
            OnDamaged = null;
        }
    }
}