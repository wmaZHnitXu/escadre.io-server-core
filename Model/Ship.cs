// File: Scripts/Server/Core/Model/Ship.cs
using System;
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    public class Ship : DestructibleEntity
    {
        // --- Properties ---
        public int ShipDesignId { get; private set; } // Identifies the type/class of ship (e.g., frigate, destroyer)
        public int OwningEscadreClientId { get; private set; } // The ClientId of the Escadre this ship belongs to

        public float CurrentSpeed { get; private set; }
        public float MaxSpeed { get; private set; }
        public float TurnRate { get; private set; } // Degrees per second or similar

        // Combat properties
        public float AttackDamage { get; private set; }
        public float AttackRange { get; private set; }
        public float AttackCooldown { get; private set; }
        private float _currentAttackCooldown = 0f;

        // Movement target (local to the ship, might be set by Escadre)
        private Vector2? _movementTargetPosition;
        // Attack order details
        private int? _targetEscadreOwnerClientId; // The ClientID of the escadre to target

        public override EntityTypeEnum EntityType => EntityTypeEnum.Ship;

        public Ship(Level level, int ownerEscadreClientId, int shipDesignId, Vector3 initialPosition, float maxHealth = 100f)
            : base(level, maxHealth)
        {
            OwningEscadreClientId = ownerEscadreClientId;
            ShipDesignId = shipDesignId; // Load stats based on this ID later
            Position = initialPosition;

            // TODO: Load these stats from a ShipDesignData store based on shipDesignId
            MaxSpeed = 5f; // Example
            TurnRate = 90f; // Example
            AttackDamage = 10f; // Example
            AttackRange = 20f; // Example
            AttackCooldown = 2f; // Example
        }

        public override void Update(float delta)
        {
            base.Update(delta); // Call base Entity update if any

            if (IsDead) return;

            UpdateMovement(delta);
            UpdateAttack(delta);

            if (_currentAttackCooldown > 0)
            {
                _currentAttackCooldown -= delta;
            }
        }

        // --- Public Methods for Escadre/System Interaction ---

        /// <summary>
        /// Sets the ship's individual movement target. Usually directed by its Escadre.
        /// </summary>
        public void SetMovementTarget(Vector2? target)
        {
            _movementTargetPosition = target;
            // Logger.Log($"[Ship {Id}] New movement target: {target}");
        }

        /// <summary>
        /// Assigns an attack order to target ships from a specific enemy escadre.
        /// </summary>
        public void AssignAttackOrder(int? targetEscadreOwnerClientId)
        {
            _targetEscadreOwnerClientId = targetEscadreOwnerClientId;
             if(targetEscadreOwnerClientId.HasValue)
                 Logger.Log($"[Ship {Id}] Assigned attack order for escadre of client: {targetEscadreOwnerClientId.Value}");
             else
                 Logger.Log($"[Ship {Id}] Attack order cancelled.");
        }

        /// <summary>
        /// Attempts to perform an upgrade.
        /// </summary>
        public void PerformUpgrade()
        {
            // TODO: Check resources from Escadre, apply upgrade from ShipDesignData
            Logger.Log($"[Ship {Id}] PerformUpgrade called. (NotImplemented)");
            throw new NotImplementedException("Ship.PerformUpgrade");
        }

        // --- Internal Logic ---

        protected virtual void UpdateMovement(float delta)
        {
            if (!_movementTargetPosition.HasValue)
            {
                CurrentSpeed = 0f; // Stop if no target
                return;
            }

            Vector2 currentPos2D = new Vector2(Position.X, Position.Z);
            Vector2 targetPos2D = _movementTargetPosition.Value;
            Vector2 directionToTarget = (targetPos2D - currentPos2D).Normalized;

            if ((targetPos2D - currentPos2D).SqrMagnitude < 0.1f * 0.1f) // Close enough
            {
                _movementTargetPosition = null; // Arrived
                CurrentSpeed = 0f;
                Position = new Vector3(targetPos2D.X, Position.Y, targetPos2D.Y); // Snap to target
                return;
            }

            // --- Basic Movement & Rotation ---
            // TODO: Implement proper steering behaviors (e.g., seek, arrive) and smooth rotation
            CurrentSpeed = MaxSpeed; // Simplified: always max speed when moving

            // Target direction for rotation
            Vector3 targetForward = new Vector3(directionToTarget.X, 0, directionToTarget.Y);
            if (targetForward.SqrMagnitude > Primitives.Vector3.Epsilon * Primitives.Vector3.Epsilon) // Ensure not zero vector
            {
                Quaternion targetRotation = Primitives.Quaternion.LookRotation(targetForward, Primitives.Vector3.Up);
                Rotation = Primitives.Quaternion.Slerp(Rotation, targetRotation, TurnRate * delta / Primitives.Quaternion.Angle(Rotation, targetRotation)); // Simplified slerp based turnrate
            }

            // Move
            Vector3 velocity = new Vector3(directionToTarget.X, 0, directionToTarget.Y) * CurrentSpeed * delta;
            Position += velocity;

            // Logger.Log($"[Ship {Id}] Moving. Pos: {Position}, Target: {_movementTargetPosition}");
        }


        protected virtual void UpdateAttack(float delta)
        {
            if (!_targetEscadreOwnerClientId.HasValue || _currentAttackCooldown > 0f)
            {
                return; // No attack order or on cooldown
            }

            // Logger.Log($"[Ship {Id}] UpdateAttack called for target escadre {_targetEscadreOwnerClientId.Value}. (Actual targeting NotImplemented)");
            // TODO: Implement targeting logic:
            // 1. Get its Escadre via _level.TryGetEscadre(this.OwningEscadreClientId).
            // 2. Use Escadre to find the target Escadre via _level.TryGetEscadre(_targetEscadreOwnerClientId.Value).
            // 3. If target Escadre found, get its list of Ship IDs.
            // 4. For each enemy Ship ID, get the Entity from _level.TryGetEntity().
            // 5. Find the closest, living, in-range enemy Ship.
            // 6. If found:
            //    Logger.Log($"[Ship {Id}] Attacking target Ship {enemyShip.Id}!");
            //    enemyShip.ApplyDamage(new DamageInfo(AttackDamage, DamageType.Kinetic, enemyShip.Position, (Position - enemyShip.Position).Normalized, Id, OwningEscadreClientId));
            //    _currentAttackCooldown = AttackCooldown;
            throw new NotImplementedException("Ship.UpdateAttack - Targeting logic");
        }

        public override void ApplyDamage(DamageInfo damageInfo)
        {
            base.ApplyDamage(damageInfo); // Applies damage, calls OnDamaged, checks for death
            // Ship-specific reaction to damage (e.g., visual effects) could be triggered here
        }

        protected override void Death()
        {
            base.Death(); // Common DestructibleEntity death logic
            // Ship-specific death logic (e.g., explosion, debris)
            Logger.Log($"[Ship {Id}] BOOM! Ship destroyed.");

            // Notify its Escadre that it died
            if(_level.TryGetEscadre(OwningEscadreClientId, out Escadre escadre))
            {
                escadre.HandleShipDestroyed(this.Id);
            }
        }
    }
}