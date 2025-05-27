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
            DefaultCannon,
            ResourceBox
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
            _level = level ?? throw new ArgumentNullException(nameof(level));
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
                FloatingBehavior.ApplyFloating(
                    this.Position, 
                    this.Rotation, 
                    _level.CurrentTime, // Pass current time from level
                    delta, 
                    out Vector3 newPos, 
                    out Quaternion newRot
                );
                this.Position = newPos;
                this.Rotation = newRot;
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