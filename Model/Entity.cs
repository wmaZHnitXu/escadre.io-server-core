using System;
using Server.Core.Primitives;

namespace Server.Core.Model
{
    public abstract class Entity
    {   public enum EntityTypeEnum {
            Debug
        }
        public abstract EntityTypeEnum EntityType { get; }
        public int Id { get; private set; }
        protected readonly Level _level;
        private Vector3 _position;
        public virtual Vector3 Position
        {
            get => _position;
            set => _position = value;
        }

        private Quaternion _rotation;
        public Quaternion Rotation
        {
            get => _rotation;
            protected set => _rotation = value;
        }

        public bool IsDead { get; private set; }

        /// <summary>
        /// <code>(Entity theDyingOne)</code> 
        /// Obligatory final OnDeath before it will be removed from the level.
        /// </summary>
        public event Action<Entity> OnDeathEvent;

        /// <summary>
        /// <code>(Entity theDyingAloudOne)</code> 
        /// May not be called if this is the silent removal.
        /// Made for kinda destruction sequences.
        /// </summary>
        public event Action<Entity> OnDestructionEvent;

        public Entity(Level level)
        {
            _level = level;
            _level.AddEntity(this);
        }

        public void AssignId(int id) {
            Id = id;
        }

        public virtual void Update(float delta)
        {

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

        protected virtual void ObligatoryOnRemove()
        {
            // To break any dependencies inside of the model before removal
        }

        protected virtual void Death()
        {
            // Destruction sequence (Ship explodes -> Damage entities near)
        }
    }
}

