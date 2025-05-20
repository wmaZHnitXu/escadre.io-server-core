using System.Collections.Generic;
using Core.Logging;

namespace Core.Model
{
    public class Level
    {
        private List<Entity> _entities;
        private List<Entity> _toRemove = new();
        private List<Entity> _toAdd = new();
        private Queue<int> _idsFreed = new();
        private int _maxIdAllocated = 0;

        private Dictionary<int, Escadre> _escadresByOwnerId = new ();

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
            entity.AssignId(GetNextId());
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

        public IEnumerable<Entity> GetAllEntities() {
            return _entities;
        }

        public bool TryGetEntity(int entityId, out Entity result) {
            for (int i = 0; i < _entities.Count; i++) {
                if (_entities[i].Id == entityId) {
                    result = _entities[i];
                    return true;
                }
            }
            result = null;
            return false;
        }

        public bool AddEscadre(Escadre escadre)
        {
            if (escadre == null || _escadresByOwnerId.ContainsKey(escadre.OwnerClientId))
            {
                Logger.LogWarning($"[Level] Failed to add escadre for Client {escadre?.OwnerClientId}. Already exists or null.");
                return false;
            }
            _escadresByOwnerId.Add(escadre.OwnerClientId, escadre);
            // if (!_activeEscadres.Contains(escadre)) _activeEscadres.Add(escadre); // If using list
            Logger.Log($"[Level] Added Escadre for Client {escadre.OwnerClientId}.");
            return true;
        }

        public bool TryGetEscadre(int ownerClientId, out Escadre escadre)
        {
            return _escadresByOwnerId.TryGetValue(ownerClientId, out escadre);
        }
        public IEnumerable<Escadre> GetAllEscadres() // For iterating all escadres if needed
        {
            return _escadresByOwnerId.Values;
        }

        public bool RemoveEscadre(int ownerClientId)
        {
            if (_escadresByOwnerId.Remove(ownerClientId, out Escadre escadre))
            {
                // if (_activeEscadres.Contains(escadre)) _activeEscadres.Remove(escadre); // If using list
                Logger.Log($"[Level] Removed Escadre for Client {ownerClientId}.");
                return true;
            }
            Logger.LogWarning($"[Level] Failed to remove escadre for Client {ownerClientId}. Not found.");
            return false;
        }

        protected void AddAddedEntities()
        {
            foreach (Entity entity in _toAdd)
            {
                if (entity.IsDead)
                {
                    _idsFreed.Enqueue(entity.Id);
                    continue;
                }
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
