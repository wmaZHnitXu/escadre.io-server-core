// File: Scripts/Server/Core/Visibility/DummyVisibilityStrategy.cs
using System.Collections.Generic;
using System.Linq;
using Core.Model;
using Core.Primitives;
using Core.Logging;

namespace Core.Visibility
{
    /// <summary>
    /// Very basic visibility strategy: everything is visible to everyone within range.
    /// Does not use any spatial optimization. O(N*M) for updates potentially.
    /// For testing purposes only.
    /// </summary>
    public class DummyVisibilityStrategy : IVisibilityStrategy
    {
        private readonly Dictionary<int, Entity> _entities = new();
        private readonly Dictionary<int, IClientView> _clients = new();

        public void AddOrUpdateEntity(Entity entity) { if(entity!=null) _entities[entity.Id] = entity; }
        public void RemoveEntity(Entity entity) { if(entity!=null) _entities.Remove(entity.Id); }
        public void AddOrUpdateClientView(IClientView clientView) { if(clientView!=null) _clients[clientView.ClientId] = clientView; }
        public void RemoveClientView(IClientView clientView) { if(clientView!=null) _clients.Remove(clientView.ClientId); }

        public IEnumerable<int> FindVisibleEntities(IClientView clientView)
        {
            if (clientView == null) yield break;

            float radiusSqr = clientView.RadiusOfInterest * clientView.RadiusOfInterest;
            foreach (var entity in _entities.Values)
            {
                if (entity == null || entity.IsDead) continue; // Skip dead/null
                // Simple distance check
                if ((entity.Position - clientView.Position).SqrMagnitude <= radiusSqr)
                {
                    yield return entity.Id;
                }
            }
        }

        public IEnumerable<int> FindObservingClients(Vector3 position, float radius)
        {
             if (radius <= 0) yield break;
             float radiusSqr = radius * radius;
             foreach (var clientView in _clients.Values)
             {
                  if ((clientView.Position - position).SqrMagnitude <= radiusSqr)
                  {
                      yield return clientView.ClientId;
                  }
             }
        }

        public void Clear() { _entities.Clear(); _clients.Clear(); }
        public void Dispose() { Clear(); Logger.Log("[DummyVisibilityStrategy] Disposed."); }
    }
}