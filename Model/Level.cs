// File: Core/Model/Level.cs
using System.Collections.Generic;
using Core.Logging;
using Core.Model;
using System.Linq;
using System; // Required for .Any() and .OfType<T>()
using Core.Ocean; 
using Core.Primitives; 

namespace Core.Model
{
    public class Level
    {
        private List<Entity> _entities;
        private List<Entity> _toRemove = new();
        private List<Entity> _toAdd = new();
        private Queue<int> _idsFreed = new();
        private int _maxIdAllocated = 0;

        public Shop GameShop { get; private set; } 

        public delegate void OnEntityAdded(Entity entity);
        public event OnEntityAdded OnEntityAddedEvent;

        public event Action<Entity> OnEntityRemovedEvent;

        private float _currentTime = 0f;
        public float CurrentTime => _currentTime;

        public IOceanDataProvider OceanDataProvider { get; private set; }
        // OceanSettings is now part of IOceanDataProvider.Settings
        // public OceanSettings OceanSettings { get; private set; }


        public Level(IOceanDataProvider oceanDataProvider = null) // OceanSettings removed from constructor
        {
            _entities = new List<Entity>();
            
            OceanDataProvider = oceanDataProvider; 
            if (OceanDataProvider == null)
            {
                Logger.LogWarning("[Level] No IOceanDataProvider provided. Ocean effects will be disabled.");
            }
            else
            {
                OceanSettings settings = OceanDataProvider.Settings; // Get settings from provider
                Logger.Log($"[Level] Ocean Initialized. TileSize: {settings.TextureTileWorldSize}, LoopDur: {settings.TextureTimeLoopDuration}, Scale: {settings.DisplacementScale}");
            }

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

            var entitiesToUpdate = _entities.ToList(); 
            foreach (Entity entity in entitiesToUpdate)
            {
                if (!_entities.Contains(entity) || entity.IsDead) continue; 
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
            var entitiesToDestroy = new List<Entity>(_entities); 
            foreach (Entity entity in entitiesToDestroy)
            {
                entity.Kill(true); 
            }
            RemoveRemovedEntities(); 
            _toAdd.Clear();
        }

        public IEnumerable<Entity> GetAllEntities() {
            return _entities;
        }
        
        public IEnumerable<Escadre> GetAllEscadres()
        {
            return _entities.OfType<Escadre>();
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
                bool removed = _entities.Remove(entity); 
                if (removed) 
                {
                    OnEntityRemovedEvent?.Invoke(entity); 
                }
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