using System;
using Core.Primitives;

namespace Core.Model
{
    public abstract class AttachedEntity<TOwner> : Entity where TOwner : Entity
    {
        public TOwner Owner { get; }
        public Vector3 LocalPositionOffset { get; set; }
        public Quaternion LocalRotationOffset { get; set; }

        public override Vector3 Position
        {
            get
            {
                if (Owner == null || Owner.IsDead)
                {
                    // If owner is gone, return the last known local offset from world origin,
                    // or simply its own _position if we decide to store it directly.
                    // For now, let's assume it becomes fixed in space based on its local offset.
                    // This behavior might need refinement based on game logic (e.g., fall down).
                    return base.Position; 
                }
                return Owner.Position + (Owner.Rotation * LocalPositionOffset);
            }
            set
            {
                if (Owner == null || Owner.IsDead)
                {
                    base.Position = value; // Store it directly if no owner or owner is dead
                    return;
                }
                // Calculate the new local offset based on the owner's current transform
                LocalPositionOffset = Owner.Rotation.Inverse * (value - Owner.Position);
                // The base _position field will be updated by the getter when next accessed,
                // or we could explicitly set it here if there's a direct backing field update needed.
                // For now, relying on the getter's calculation.
                // We also need to ensure the base._position is updated for the OnTeleported event.
                base.Position = value;
            }
        }

        public override Quaternion Rotation
        {
            get
            {
                if (Owner == null || Owner.IsDead)
                {
                    return base.Rotation; // Return its own rotation if no owner or owner is dead
                }
                return Owner.Rotation * LocalRotationOffset;
            }
            protected set
            {
                if (Owner == null || Owner.IsDead)
                {
                    base.Rotation = value;
                    return;
                }
                LocalRotationOffset = Owner.Rotation.Inverse * value;
                base.Rotation = value; 
            }
        }

        protected AttachedEntity(Level level, TOwner owner, Vector3 localPositionOffset, Quaternion localRotationOffset)
            : base(level)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            LocalPositionOffset = localPositionOffset;
            LocalRotationOffset = localRotationOffset;

            // Initialize base position and rotation to the calculated world values
            // This is important because Entity.TeleportTo and other mechanisms might use the base fields.
            base.Position = Owner.Position + (Owner.Rotation * LocalPositionOffset);
            base.Rotation = Owner.Rotation * LocalRotationOffset;

            Owner.OnDeathEvent += HandleOwnerDeath;
            Owner.OnTeleported += HandleOwnerTeleported; // Subscribe to owner's teleport event
        }

        private void HandleOwnerDeath(Entity ownerEntity)
        {
            if (ownerEntity == Owner)
            {
                // Owner is dead, kill this attached entity silently.
                // "Silent" because the owner's death is the primary event.
                this.Kill(true);
            }
        }
        
        private void HandleOwnerTeleported(Entity ownerEntity, Vector3 newOwnerPosition, Quaternion newOwnerRotation)
        {
            if (ownerEntity == Owner && !this.IsDead)
            {
                // Owner teleported. This AttachedEntity's world position and rotation also change.
                // We don't need to change LocalPositionOffset or LocalRotationOffset.
                // We just need to update our base position/rotation and fire our own OnTeleported event.
                Vector3 newWorldPosition = newOwnerPosition + (newOwnerRotation * LocalPositionOffset);
                Quaternion newWorldRotation = newOwnerRotation * LocalRotationOffset;

                // Directly call base.TeleportTo to update internal fields and raise event.
                // This avoids the logic in our overridden Position/Rotation setters.
                base.TeleportTo(newWorldPosition, newWorldRotation);
            }
        }

        public override void TeleportTo(Vector3 newWorldPosition, Quaternion newWorldRotation)
        {
            if (IsDead) return;

            if (Owner != null && !Owner.IsDead)
            {
                // If we have a valid owner, setting the world position/rotation
                // should update our local offsets relative to the owner.
                LocalPositionOffset = Owner.Rotation.Inverse * (newWorldPosition - Owner.Position);
                LocalRotationOffset = Owner.Rotation.Inverse * newWorldRotation;
            }
            // else, if no owner, the local offsets are effectively world offsets from origin.
            // LocalPositionOffset = newWorldPosition;
            // LocalRotationOffset = newWorldRotation;
            // This case is tricky: if ownerless, how should these be interpreted?
            // For now, the Position and Rotation setters handle the ownerless case by updating base fields.
            
            // Call the base implementation to update _position, _rotation and fire the OnTeleported event.
            base.TeleportTo(newWorldPosition, newWorldRotation);
        }

        protected override void ObligatoryOnRemove()
        {
            base.ObligatoryOnRemove();
            if (Owner != null)
            {
                Owner.OnDeathEvent -= HandleOwnerDeath;
                Owner.OnTeleported -= HandleOwnerTeleported;
            }
        }
    }
} 