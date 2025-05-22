// File: Core/Model/Level.cs
using System.Collections.Generic;
using Core.Logging;
using Core.Model;
using System.Linq; // Required for .Any() and .OfType<T>()

namespace Core.Model
{
    public class Level
    {
        private List<Entity> _entities;
        private List<Entity> _toRemove = new();
        private List<Entity> _toAdd = new();
        private Queue<int> _idsFreed = new();
        private int _maxIdAllocated = 0;

        // private Dictionary<int, Escadre> _escadresByOwnerId = new (); // Removed: Escadres are now Entities
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

            // Update individual entities (ships, projectiles, escadres, etc.)
            // Iterate a copy because an entity's Update might lead to adding/removing other entities
            var entitiesToUpdate = _entities.ToList(); 
            foreach (Entity entity in entitiesToUpdate)
            {
                // Ensure entity wasn't removed during this same update tick by a previous entity's update
                if (!_entities.Contains(entity) || entity.IsDead) continue; 
                entity.Update(delta);
            }

            // Escadre updates are now handled by the generic Entity.Update loop
            // foreach(var escadre in _escadresByOwnerId.Values) // Removed
            // {
            //    escadre.Update(delta, _currentTime);
            // }

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
            // Kill all entities, including Escadres.
            // Escadre.Death() or Escadre.ObligatoryOnRemove() should handle disbanding their ships.
            var entitiesToDestroy = new List<Entity>(_entities); // Iterate a copy for modification
            foreach (Entity entity in entitiesToDestroy)
            {
                entity.Kill(true); // Kill silently
            }
            // Ensure _toRemove processes these killed entities
            RemoveRemovedEntities(); 
            // AddAddedEntities might try to add entities that were queued but whose "creator" was destroyed.
            // Clear _toAdd to prevent issues, or ensure AddAddedEntities checks IsDead.
            _toAdd.Clear();


            // The old escadre-specific cleanup is no longer needed here as Escadres are entities.
            // var escadreOwners = new List<int>(_escadresByOwnerId.Keys);
            // foreach(var ownerId in escadreOwners)
            // {
            // if (_escadresByOwnerId.TryGetValue(ownerId, out var escadre))
            // {
            // escadre.Disband(true); 
            // }
            // RemoveEscadre(ownerId); 
            // }
            // _escadresByOwnerId.Clear(); 
        }

        public IEnumerable<Entity> GetAllEntities() {
            return _entities;
        }
        
        public IEnumerable<Escadre> GetAllEscadres()
        {
            return _entities.OfType<Escadre>();
        }

        public bool TryGetEntity(int entityId, out Entity result) {
            // Consider using a Dictionary<int, Entity> for _entities if performance becomes an issue.
            // For now, List iteration is fine for moderate numbers of entities.
            for (int i = 0; i < _entities.Count; i++) {
                if (_entities[i].Id == entityId) {
                    result = _entities[i];
                    return true;
                }
            }
            result = null;
            return false;
        }
        
        // Replaces old AddEscadre. Escadres are added via AddEntity.
        // public bool AddEscadre(Escadre escadre) // REMOVED

        // Replaces old TryGetEscadre
        public bool TryGetEscadreForOwner(int ownerClientId, out Escadre escadre)
        {
            escadre = _entities.OfType<Escadre>().FirstOrDefault(e => e.OwnerClientId == ownerClientId && !e.IsDead);
            return escadre != null;
        }
        
        public bool TryGetEscadreByEntityId(int escadreEntityId, out Escadre escadre)
        {
            if (TryGetEntity(escadreEntityId, out Entity entity) && entity is Escadre castedEscadre)
            {
                escadre = castedEscadre;
                return !escadre.IsDead;
            }
            escadre = null;
            return false;
        }

        // Replaces old RemoveEscadre. Escadres are removed by finding them as entities and calling entity.Kill().
        // public bool RemoveEscadre(int ownerClientId) // REMOVED

        protected void AddAddedEntities()
        {
            if (!_toAdd.Any()) return;

            foreach (Entity entity in _toAdd)
            {
                if (entity.IsDead) // If entity was killed before it was even added
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
            // This is called when an entity's IsDead becomes true and OnDeathEvent fires.
            // The entity is already marked as dead. We just need to schedule its removal from the _entities list.
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