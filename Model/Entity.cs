// File: Core/Model/Entity.cs
using System;
using Core.Primitives;
using Core.Ocean; 

namespace Core.Model
{
    public abstract class Entity
    {
        public enum EntityTypeEnum
        {
            Debug,
            Escadre,
            DefaultShip,
            DefaultCannon
        }
        public abstract EntityTypeEnum EntityType { get; }
        public int Id { get; private set; }
        protected readonly Level _level;
        public Level Level => _level;
        private Vector3 _position;
        public virtual Vector3 Position
        {
            get => _position;
            set => _position = value;
        }

        private Quaternion _rotation;
        public virtual Quaternion Rotation
        {
            get => _rotation;
            set => _rotation = value;
        }

        public bool IsDead { get; private set; }

        public IFloatingBehavior FloatingBehavior { get; set; }

        public event Action<Entity> OnDeathEvent;
        public event Action<Entity> OnDestructionEvent;
        public event Action<Entity, Vector3, Quaternion> OnTeleported;

        public Entity(Level level)
        {
            _level = level ?? throw new ArgumentNullException(nameof(level)); // Ensure level is not null
            _level.AddEntity(this);
        }

        public void AssignId(int id)
        {
            Id = id;
        }

        public virtual void Update(float delta)
        {
            if (IsDead) return;

            if (FloatingBehavior != null && _level.OceanDataProvider != null)
            {
                // The entity's "base" XZ from which to sample the ocean.
                // For simple entities, this is just Position.X and Position.Z.
                // For entities whose movement is planar and then Y is adjusted (like ships),
                // this XZ is their current planar position.
                float sampleX = Position.X;
                float sampleZ = Position.Z;

                // Get ocean displacement and normal at the entity's current "base" XZ position
                Vector3 oceanDisplacement = _level.OceanDataProvider.GetDisplacement(sampleX, sampleZ, _level.CurrentTime);
                Vector3 oceanNormal = _level.OceanDataProvider.GetNormal(sampleX, sampleZ, _level.CurrentTime);

                // The oceanSurfacePoint is the entity's current base XZ plus the ocean's full displacement vector.
                // This is the point on the surface that corresponds to the entity's footprint.
                Vector3 targetSurfacePoint = new Vector3(
                    sampleX + oceanDisplacement.X, 
                    oceanDisplacement.Y,           // The Y component of displacement is the ocean surface height relative to Y=0
                    sampleZ + oceanDisplacement.Z  
                );
                
                FloatingBehavior.ApplyFloating(this, targetSurfacePoint, oceanNormal, delta);
            }
        }

        public void Kill(bool silent = false)
        {
            if (IsDead) return;
            IsDead = true;

            if (!silent)
            {
                Death();
                OnDestructionEvent?.Invoke(this);
                OnDestructionEvent = null;
            }

            OnDeathEvent?.Invoke(this);
            OnDeathEvent = null;
            ObligatoryOnRemove();
        }

        public virtual void TeleportTo(Vector3 newPosition, Quaternion newRotation)
        {
            if (IsDead)
            {
                return;
            }

            _position = newPosition;
            _rotation = newRotation;
            OnTeleported?.Invoke(this, _position, _rotation);
        }

        protected virtual void ObligatoryOnRemove()
        {
            FloatingBehavior = null; 
        }

        protected virtual void Death()
        {
        }
    }
}