// File: Core/Model/Level.cs
using System.Collections.Generic;
using Core.Logging;
using Core.Model;
using System.Linq; // Required for .Any()

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
        public Shop GameShop { get; private set; } 

        public delegate void OnEntityAdded(Entity entity);
        public event OnEntityAdded OnEntityAddedEvent;

        private float _currentTime = 0f;
        public float CurrentTime => _currentTime;


        public Level()
        {
            _entities = new List<Entity>();
            InitializeShop(); 
        }

        private void InitializeShop()
        {
            var designs = new List<ShipDesign>
            {
                new ShipDesign(1, "Default Light Ship", 100, Entity.EntityTypeEnum.DefaultShip),
            };
            GameShop = new Shop(designs);
            Logger.Log("[Level] GameShop initialized with default designs.");
        }


        public void DoUpdate(float delta)
        {
            _currentTime += delta;

            // Update individual entities (ships, projectiles, etc.)
            foreach (Entity entity in _entities) // Iterate a copy if modification during iteration is possible
            {
                entity.Update(delta);
            }

            // Update escadres (for formation anchor movement, etc.)
            foreach(var escadre in _escadresByOwnerId.Values)
            {
                escadre.Update(delta, _currentTime); // Pass serverTime
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
            var escadreOwners = new List<int>(_escadresByOwnerId.Keys);
            foreach(var ownerId in escadreOwners)
            {
                if (_escadresByOwnerId.TryGetValue(ownerId, out var escadre))
                {
                    escadre.Disband(true); 
                }
                RemoveEscadre(ownerId); 
            }
             _escadresByOwnerId.Clear(); 
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
            Logger.Log($"[Level] Added Escadre for Client {escadre.OwnerClientId}.");
            return true;
        }

        public bool TryGetEscadre(int ownerClientId, out Escadre escadre)
        {
            return _escadresByOwnerId.TryGetValue(ownerClientId, out escadre);
        }
        public IEnumerable<Escadre> GetAllEscadres()
        {
            return _escadresByOwnerId.Values;
        }

        public bool RemoveEscadre(int ownerClientId)
        {
            if (_escadresByOwnerId.Remove(ownerClientId, out Escadre escadre))
            {
                Logger.Log($"[Level] Removed Escadre for Client {ownerClientId}.");
                return true;
            }
            Logger.LogWarning($"[Level] Failed to remove escadre for Client {ownerClientId}. Not found.");
            return false;
        }

        protected void AddAddedEntities()
        {
            if (!_toAdd.Any()) return;

            foreach (Entity entity in _toAdd)
            {
                if (entity.IsDead) 
                {
                    _idsFreed.Enqueue(entity.Id); 
                    continue;
                }
                _entities.Add(entity);
                entity.OnDeathEvent += HandleEntityDeathForLevelCleanup; 
                OnEntityAddedEvent?.Invoke(entity); 
            }
            _toAdd.Clear();
        }
        
        private void HandleEntityDeathForLevelCleanup(Entity entity)
        {
            RemoveEntity(entity);
        }


        protected void RemoveRemovedEntities()
        {
            if (!_toRemove.Any()) return;

            foreach (Entity entity in _toRemove)
            {
                entity.OnDeathEvent -= HandleEntityDeathForLevelCleanup; 
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