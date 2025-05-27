// File: Core/Model/CollectableFloatingEntity.cs
using System;
using System.Linq;
using Core.Primitives;
using Core.Logging;
using Core.Ocean;

namespace Core.Model
{
    public abstract class CollectableFloatingEntity : Entity
    {
        /// <summary>
        /// The distance at which the collectable is considered "picked up" when being sucked towards a ship.
        /// </summary>
        public float FinalCollectionDistance { get; protected set; }

        /// <summary>
        /// Speed at which the collectable moves towards the collecting ship.
        /// </summary>
        public float SuckSpeed { get; protected set; }

        /// <summary>
        /// Event fired on the server when this entity is collected.
        /// Parameters: CollectableFloatingEntity (self), Ship (collector)
        /// </summary>
        public event Action<CollectableFloatingEntity, Ship> OnCollectedServerEvent;

        protected Ship _collectingShip = null;
        public Ship CollectingShip => _collectingShip;


        protected CollectableFloatingEntity(Level level, Vector3 initialPosition, float finalCollectionDistance = 0.75f, float suckSpeed = 4.0f)
            : base(level)
        {
            Position = initialPosition;
            Rotation = Quaternion.Identity;
            FinalCollectionDistance = finalCollectionDistance;
            SuckSpeed = suckSpeed;
        }

        public virtual bool TryClaimBy(Ship potentialCollector)
        {
            if (IsDead || _collectingShip != null || potentialCollector == null || potentialCollector.IsDead)
            {
                return false; // Already claimed, dead, or invalid collector
            }

            _collectingShip = potentialCollector;
            // Logger.Log($"[CollectableFloatingEntity {Id}] Claimed by Ship {potentialCollector.Id}.");
            // Subscribe to the ship's death event to unclaim if it dies.
            potentialCollector.OnDeathEvent += HandleCollectingShipDeath;
            return true;
        }

        public virtual void Unclaim()
        {
            if (_collectingShip != null)
            {
                // Logger.Log($"[CollectableFloatingEntity {Id}] Unclaimed from Ship {_collectingShip.Id}.");
                _collectingShip.OnDeathEvent -= HandleCollectingShipDeath; // Unsubscribe
                _collectingShip = null;
            }
        }

        private void HandleCollectingShipDeath(Entity deadShip)
        {
            if (deadShip == _collectingShip)
            {
                // Logger.Log($"[CollectableFloatingEntity {Id}] Collecting Ship {deadShip.Id} died. Unclaiming.");
                Unclaim();
            }
        }

        public override void Update(float delta)
        {
            base.Update(delta); // Handles floating behavior

            if (IsDead) return;

            if (_collectingShip != null)
            {
                if (_collectingShip.IsDead) // Double check if ship died and event didn't propagate yet or was missed
                {
                    Unclaim();
                    return;
                }

                Vector3 directionToShip = _collectingShip.Position - this.Position;
                float distanceToShipSq = directionToShip.SqrMagnitude;

                if (distanceToShipSq <= FinalCollectionDistance * FinalCollectionDistance)
                {
                    Collect(_collectingShip);
                }
                else
                {
                    // Interpolate position towards the ship
                    // Using MoveTowards for consistent speed
                    float step = SuckSpeed * delta;
                    this.Position = Vector3.Lerp(this.Position, _collectingShip.Position, step / MathF.Sqrt(distanceToShipSq));
                    // A more robust MoveTowards:
                    // this.Position += directionToShip.Normalized * Math.Min(step, MathF.Sqrt(distanceToShipSq));

                }
            }
        }

        protected virtual void Collect(Ship collectingShip)
        {
            if (IsDead) return; // Should not happen if logic is correct, but good check

            // Logger.Log($"[CollectableFloatingEntity {Id}] Collected by Ship {collectingShip.Id} (Owner: {collectingShip.OwningEscadreClientId})");
            
            ApplyPickupEffect(collectingShip);

            OnCollectedServerEvent?.Invoke(this, collectingShip);
            
            Unclaim(); // Ensure we unclaim before killing, to remove event subscriptions
            this.Kill(true); // Kill silently on server model
        }

        /// <summary>
        /// Abstract method to be implemented by derived classes to define
        /// what happens when this collectable is picked up by a ship.
        /// </summary>
        /// <param name="collectingShip">The ship that collected this item.</param>
        protected abstract void ApplyPickupEffect(Ship collectingShip);

        protected override void ObligatoryOnRemove()
        {
            base.ObligatoryOnRemove();
            Unclaim(); // Ensure unclaim on removal (e.g. if level forces removal)
            OnCollectedServerEvent = null;
        }
    }
}