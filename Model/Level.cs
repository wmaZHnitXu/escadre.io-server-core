using System.Collections.Generic;

namespace Server.Core.Model
{
    public class Level
    {
        private List<Entity> _entities;
        private List<Entity> _toRemove = new();
        private List<Entity> _toAdd = new();
        private Queue<int> _idsFreed = new();
        private int _maxIdAllocated = 0;

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


        protected void AddAddedEntities()
        {
            foreach (Entity entity in _toAdd)
            {
                if (entity.IsDead) continue;
                entity.AssignId(GetNextId());
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
                _idsFreed.Enqueue(entity.Id);
            }
            _toRemove.Clear();
        }

        private int GetNextId()
        {
            if (_idsFreed.Count == 0)
            {
                return ++_maxIdAllocated;
            }
            else
            {
                return _idsFreed.Dequeue();
            }
        }
    }
}
