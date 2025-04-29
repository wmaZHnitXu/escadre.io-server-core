using System.Collections.Generic;

namespace Server.Core.Model
{
    public class Level
    {
        private List<Entity> _entities;
        private List<Entity> _toRemove = new List<Entity>();
        private List<Entity> _toAdd = new List<Entity>();

        public delegate void OnEntityAdded(Entity entity);
        public event OnEntityAdded OnEntityAddedEvent;

        public Level()
        {
            _entities = new List<Entity>();
        }

        public void DoUpdate(float delta)
        {
            foreach (Entity entity in _entities)
            {
                entity.Update(delta);
            }

            RemoveRemovedEntities();
            AddAddedEntities();
        }

        public void AddEntity(Entity entity)
        {
            _toAdd.Add(entity);
        }


        protected void AddAddedEntities()
        {
            foreach (Entity entity in _toAdd)
            {
                _entities.Add(entity);
                entity.OnDeathEvent += RemoveEntity;
                OnEntityAddedEvent(entity);
            }
            _toAdd.Clear();
        }

        protected void RemoveRemovedEntities()
        {
            foreach (Entity entity in _toRemove)
            {
                entity.OnDeathEvent -= RemoveEntity;
                _entities.Remove(entity);
            }
            _toRemove.Clear();
        }

        public void RemoveEntity(Entity entity)
        {
            _toRemove.Add(entity);
        }

        public void Destroy()
        {
            foreach (Entity entity in _entities)
            {
                entity.Kill(true);
            }
        }
    }
}
