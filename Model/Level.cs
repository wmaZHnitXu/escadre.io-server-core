// File: Core/Model/Level.cs
using System.Collections.Generic;
using Core.Logging;
using Core.Model;
using System.Linq;
using System; 
using Core.Ocean; 
using Core.Primitives; 
using Core.Visibility; // For IVisibilityStrategy

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
        private readonly IVisibilityStrategy _spatialIndex; // Changed from IVisibilityStrategy to a more direct name


        public Level(IOceanDataProvider oceanDataProvider = null, IVisibilityStrategy spatialIndex = null) 
        {
            _entities = new List<Entity>();
            
            OceanDataProvider = oceanDataProvider; 
            if (OceanDataProvider == null)
            {
                Logger.LogWarning("[Level] No IOceanDataProvider provided. Ocean effects will be disabled.");
            }
            else
            {
                OceanSettings settings = OceanDataProvider.Settings; 
                Logger.Log($"[Level] Ocean Initialized. TileSize: {settings.TextureTileWorldSize}, LoopDur: {settings.TextureTimeLoopDuration}, Scale: {settings.DisplacementScale}");
            }
            
            _spatialIndex = spatialIndex;
            if (_spatialIndex == null)
            {
                Logger.LogWarning("[Level] No IVisibilityStrategy (SpatialIndex) provided. Spatial queries will not be optimized.");
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
                Vector3 oldPosition = entity.Position; // Store position before update
                entity.Update(delta);
                // After entity.Update(), its position might have changed.
                // If spatial index is present, notify it about the potential move.
                if (_spatialIndex != null && entity.Position != oldPosition) // Only update if position actually changed
                {
                    _spatialIndex.AddOrUpdateEntity(entity);
                }
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
            _spatialIndex?.Clear();
        }

        public IEnumerable<Entity> GetAllEntities() {
            return _entities;
        }
        
        public IEnumerable<Escadre> GetAllEscadres()
        {
            return _entities.OfType<Escadre>();
        }

        public bool TryGetEntity(int entityId, out Entity result) {
            // This linear search is acceptable as _entities is the master list.
            // Spatial index is for proximity queries, not direct ID lookups.
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
                _spatialIndex?.AddOrUpdateEntity(entity); // Add to spatial index
                entity.OnDeathEvent += HandleEntityDeathForLevelCleanup; 
                OnEntityAddedEvent?.Invoke(entity); 
            }
            _toAdd.Clear();
        }
        
        private void HandleEntityDeathForLevelCleanup(Entity entity)
        {
            RemoveEntity(entity); // This will queue it for removal
        }


        protected void RemoveRemovedEntities()
        {
            if (!_toRemove.Any()) return;

            foreach (Entity entity in _toRemove)
            {
                entity.OnDeathEvent -= HandleEntityDeathForLevelCleanup; 
                _spatialIndex?.RemoveEntity(entity); // Remove from spatial index
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

        // --- Spatial Query Methods using IVisibilityStrategy ---
        public IEnumerable<Entity> GetEntitiesInRect(RectFloat areaBounds, Predicate<Entity> filter = null)
        {
            if (_spatialIndex == null)
            {
                // Fallback: linear scan if no spatial index
                Logger.LogWarning("[Level] GetEntitiesInRect: Spatial index not available, performing linear scan.");
                foreach (var entity in _entities)
                {
                    if (!entity.IsDead && entity.GetBounds2D().Intersects(areaBounds) && (filter == null || filter(entity)))
                    {
                        yield return entity;
                    }
                }
                yield break;
            }

            var entityIds = _spatialIndex.QueryRect(areaBounds);
            foreach (var id in entityIds)
            {
                if (TryGetEntity(id, out Entity entity) && !entity.IsDead && (filter == null || filter(entity)))
                {
                    yield return entity;
                }
            }
        }

        public IEnumerable<Entity> GetEntitiesInRadius(Vector2 center, float radius, Predicate<Entity> filter = null)
        {
            if (_spatialIndex == null)
            {
                // Fallback: linear scan
                Logger.LogWarning("[Level] GetEntitiesInRadius: Spatial index not available, performing linear scan.");
                float radiusSq = radius * radius;
                foreach (var entity in _entities)
                {
                    if (!entity.IsDead && 
                        (new Vector2(entity.Position.X, entity.Position.Z) - center).SqrMagnitude <= radiusSq &&
                        (filter == null || filter(entity)))
                    {
                        yield return entity;
                    }
                }
                yield break;
            }

            var entityIds = _spatialIndex.QueryRadius(center, radius);
            foreach (var id in entityIds)
            {
                if (TryGetEntity(id, out Entity entity) && !entity.IsDead && (filter == null || filter(entity)))
                {
                    yield return entity;
                }
            }
        }
    }
}