// File: Core/Visibility/QuadTreeVisibilityStrategy.cs
using Core.Model;
using Core.Primitives;
using Core.Spatial;
using Core.Logging;
using System.Collections.Generic;
using System.Linq;

namespace Core.Visibility
{
    public class QuadTreeVisibilityStrategy : IVisibilityStrategy
    {
        private readonly QuadTree<int> _entityQuadTree;
        private readonly Dictionary<int, Entity> _trackedEntities; // For quick access to Entity objects
        private readonly Dictionary<int, IClientView> _trackedClients; // For FindObservingClients

        // World bounds for the QuadTree. Should be configured based on your game world size.
        private static readonly RectFloat DefaultWorldBounds = new RectFloat(-500f, -500f, 1000f, 1000f);
        private const int DefaultNodeCapacity = 4;
        private const int DefaultMaxDepth = 8;

        public QuadTreeVisibilityStrategy(RectFloat? worldBounds = null, int nodeCapacity = DefaultNodeCapacity, int maxDepth = DefaultMaxDepth)
        {
            _entityQuadTree = new QuadTree<int>(worldBounds ?? DefaultWorldBounds, nodeCapacity, maxDepth);
            _trackedEntities = new Dictionary<int, Entity>();
            _trackedClients = new Dictionary<int, IClientView>();
            Logger.Log($"[QuadTreeVisibilityStrategy] Initialized with bounds: {worldBounds ?? DefaultWorldBounds}");
        }

        public void AddOrUpdateEntity(Entity entity)
        {
            if (entity == null) return;

            _trackedEntities[entity.Id] = entity; // Keep track for bounds, etc.
            if (!entity.IsDead) // Only add non-dead entities to the QuadTree for queries
            {
                _entityQuadTree.Insert(entity.Id, entity.GetBounds2D());
            }
            else // If it's dead, ensure it's removed
            {
                _entityQuadTree.Remove(entity.Id);
            }
        }

        public void RemoveEntity(Entity entity)
        {
            if (entity == null) return;
            _trackedEntities.Remove(entity.Id);
            _entityQuadTree.Remove(entity.Id);
        }

        public void AddOrUpdateClientView(IClientView clientView)
        {
            if (clientView == null) return;
            _trackedClients[clientView.ClientId] = clientView;
        }

        public void RemoveClientView(IClientView clientView)
        {
            if (clientView == null) return;
            _trackedClients.Remove(clientView.ClientId);
        }

        public IEnumerable<int> FindVisibleEntities(IClientView clientView)
        {
            if (clientView == null) return Enumerable.Empty<int>();

            // Query QuadTree based on client's RadiusOfInterest
            // The client's position is 3D, but QuadTree is 2D (X,Z)
            Vector2 clientPos2D = new Vector2(clientView.Position.X, clientView.Position.Z);
            return _entityQuadTree.QueryRadius(clientPos2D, clientView.RadiusOfInterest);
        }

        public IEnumerable<int> FindObservingClients(Vector3 position, float radius)
        {
            // Clients are not in the QuadTree, so iterate through tracked clients
            List<int> observingClientIds = new List<int>();
            Vector2 pos2D = new Vector2(position.X, position.Z);
            float radiusSq = radius * radius;

            foreach (var clientView in _trackedClients.Values)
            {
                Vector2 clientViewPos2D = new Vector2(clientView.Position.X, clientView.Position.Z);
                // Check if the client's PVS (a circle) overlaps with the query circle (position, radius)
                // Overlap if distance between centers <= sum of radii
                float distSqBetweenCenters = (clientViewPos2D - pos2D).SqrMagnitude;
                float sumRadii = clientView.RadiusOfInterest + radius;
                if (distSqBetweenCenters <= sumRadii * sumRadii)
                {
                    observingClientIds.Add(clientView.ClientId);
                }
            }
            return observingClientIds;
        }

        public IEnumerable<int> QueryRect(RectFloat areaBounds)
        {
            return _entityQuadTree.Query(areaBounds);
        }

        public IEnumerable<int> QueryRadius(Vector2 center, float radius)
        {
            return _entityQuadTree.QueryRadius(center, radius);
        }

        public void Clear()
        {
            _entityQuadTree.Clear();
            _trackedEntities.Clear();
            _trackedClients.Clear();
            Logger.Log("[QuadTreeVisibilityStrategy] Cleared.");
        }

        public void Dispose()
        {
            Clear();
            Logger.Log("[QuadTreeVisibilityStrategy] Disposed.");
        }
    }
}