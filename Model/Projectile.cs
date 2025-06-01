// File: Core/Model/Projectile.cs
using System;
using Core.Primitives;
using Core.Logging;
using System.Linq; // For spatial query filtering

namespace Core.Model
{
    public abstract class Projectile : Entity
    {
        public int OwnerEntityId { get; }
        public int OwnerClientId { get; } // Client ID of the Escadre that ultimately owns the firing entity
        public Vector3 Velocity { get; protected set; }
        public DamageInfo DamagePayload { get; protected set; }
        public float MaxLifetime { get; protected set; }
        protected float _currentLifetime;

        // For client-side prediction
        public Vector3 InitialPosition_Prediction { get; protected set; }
        public Vector3 InitialVelocity_Prediction { get; protected set; }
        public float ServerTimeOfSpawn_Prediction { get; protected set; }

        /// <summary>
        /// Event fired on the server when this projectile hits a destructible entity.
        /// Parameters: Projectile (self), DestructibleEntity (victim), Vector3 (hitPoint), Vector3 (hitNormal), Collider (hitCollider on victim)
        /// </summary>
        public event Action<Projectile, DestructibleEntity, Vector3, Vector3, Collider> OnHitServerEvent;

        protected Projectile(
            Level level,
            int ownerEntityId,
            int ownerClientId,
            Vector3 initialPosition,
            Vector3 initialVelocity,
            DamageInfo damagePayload,
            float maxLifetime,
            float serverTimeOfSpawn)
            : base(level)
        {
            OwnerEntityId = ownerEntityId;
            OwnerClientId = ownerClientId;
            Position = initialPosition;
            Velocity = initialVelocity;
            DamagePayload = damagePayload;
            MaxLifetime = maxLifetime;
            _currentLifetime = 0f;

            InitialPosition_Prediction = initialPosition;
            InitialVelocity_Prediction = initialVelocity;
            ServerTimeOfSpawn_Prediction = serverTimeOfSpawn;

            // Projectiles typically don't have complex rotation from movement,
            // but their initial rotation should align with their velocity.
            if (Velocity.SqrMagnitude > Vector3.Epsilon)
            {
                Rotation = Quaternion.LookRotation(Velocity.Normalized, Vector3.Up);
            }
            else
            {
                Rotation = Quaternion.Identity;
            }
        }

        public override void Update(float delta)
        {
            base.Update(delta); // Handles base Entity logic (like floating, though projectiles usually don't float)

            if (IsDead) return;

            _currentLifetime += delta;
            if (_currentLifetime >= MaxLifetime)
            {
                // Logger.Log($"[Projectile {Id}] Lifetime expired. Killing silently.");
                Kill(true); // Kill silently as it's just fizzling out
                return;
            }

            Vector3 currentPosition = Position;
            Vector3 travelVector = Velocity * delta;
            Vector3 nextPotentialPosition = currentPosition + travelVector;
            float travelDistance = travelVector.Magnitude;

            if (travelDistance < Vector3.Epsilon) // Not moving
            {
                Position = nextPotentialPosition;
                return;
            }

            // Perform Raycast
            Vector2 midPointPath = new Vector2((currentPosition.X + nextPotentialPosition.X) * 0.5f, (currentPosition.Z + nextPotentialPosition.Z) * 0.5f);
            float queryRadius = travelDistance * 0.5f + this.BoundingRadius2D + 5.0f; 

            // Broadphase query: Get all entities that *could* be hit.
            // Filter first for DestructibleEntity, then apply game-specific rules.
            var candidateEntities = _level.GetEntitiesInRadius(
                midPointPath,
                queryRadius,
                entity => // Initial coarse filter for spatial query
                {
                    if (entity.IsDead || entity.Id == this.Id || entity.Id == OwnerEntityId) return false;
                    return true; // Further detailed checks below
                });


            DestructibleEntity closestHitEntity = null;
            Collider closestHitCollider = null;
            float closestHitDistance = travelDistance + 1.0f; 
            Vector3 closestHitPoint = Vector3.Zero;
            Vector3 closestHitNormal = Vector3.Zero;

            foreach (var candidate in candidateEntities)
            {
                // Detailed filter: Must be DestructibleEntity and not friendly.
                if (!(candidate is DestructibleEntity deVictim)) continue;

                // Friendly fire check (specific to Ship type for now)
                if (deVictim is Ship shipVictim && shipVictim.OwningEscadreClientId == OwnerClientId)
                {
                    continue; // Friendly ship, skip
                }
                // If an attached entity like a cannon was destructible and distinct from its owner ship for FF,
                // a similar check would be needed for its owner.
                // Example: if (deVictim is DestructibleCannon dc && dc.Owner is Ship s && s.OwningEscadreClientId == OwnerClientId) continue;
                // For now, only Ships have OwningEscadreClientId for FF.


                // Check against each collider of the victim
                if (deVictim.CheckRayIntersection(currentPosition, Velocity.Normalized, travelDistance,
                                                out Collider hitCollider, out float dist, out Vector3 point, out Vector3 normal))
                {
                    if (dist < closestHitDistance)
                    {
                        closestHitDistance = dist;
                        closestHitPoint = point;
                        closestHitNormal = normal;
                        closestHitEntity = deVictim;
                        closestHitCollider = hitCollider;
                    }
                }
            }

            if (closestHitEntity != null)
            {
                // Projectile hit something
                Position = closestHitPoint; // Move projectile to exact hit point
                HandleHit(closestHitEntity, closestHitPoint, closestHitNormal, closestHitCollider);
                Kill(false); // Projectile is destroyed on impact (loud for potential effects)
            }
            else
            {
                // No hit, continue moving
                Position = nextPotentialPosition;
            }
        }

        protected virtual void HandleHit(DestructibleEntity victim, Vector3 hitPoint, Vector3 hitNormal, Collider hitCollider)
        {
            // Logger.Log($"[Projectile {Id}] Hit {victim.EntityType} {victim.Id} at {hitPoint}. Applying {DamagePayload.Amount} damage.");
            victim.ApplyDamage(DamagePayload);
            OnHitServerEvent?.Invoke(this, victim, hitPoint, hitNormal, hitCollider);
        }

        protected override void ObligatoryOnRemove()
        {
            base.ObligatoryOnRemove();
            OnHitServerEvent = null;
        }
    }
}