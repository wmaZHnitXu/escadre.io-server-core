// File: Scripts/Server/Core/Visibility/IVisibilityStrategy.cs
using System;
using System.Collections.Generic;
using Core.Model; // Entity
using Core.Primitives; // Vector3

namespace Core.Visibility
{
    /// <summary>
    /// Interface for spatial partitioning strategies used by the VisibilityManager.
    /// </summary>
    public interface IVisibilityStrategy : IDisposable
    {
        /// <summary>
        /// Adds or updates an entity's position in the spatial structure.
        /// </summary>
        void AddOrUpdateEntity(Entity entity);

        /// <summary>
        /// Removes an entity from the spatial structure.
        /// </summary>
        void RemoveEntity(Entity entity);

        /// <summary>
        /// Adds or updates a client view's position/radius in the spatial structure.
        /// </summary>
        void AddOrUpdateClientView(IClientView clientView);

        /// <summary>
        /// Removes a client view from the spatial structure.
        /// </summary>
        void RemoveClientView(IClientView clientView);

        /// <summary>
        /// Finds all entities potentially visible to a specific client view.
        /// </summary>
        /// <param name="clientView">The client view to query for.</param>
        /// <returns>A collection of entity IDs potentially within the client's view.</returns>
        IEnumerable<int> FindVisibleEntities(IClientView clientView);

        /// <summary>
        /// Finds all client views potentially observing a specific area (e.g., around an entity).
        /// Useful for optimizing updates when an entity moves.
        /// </summary>
        /// <param name="position">Center position of the query area.</param>
        /// <param name="radius">Radius of the query area.</param>
        /// <returns>A collection of client IDs potentially observing the area.</returns>
        IEnumerable<int> FindObservingClients(Vector3 position, float radius);

         /// <summary>
        /// Optional: Clear all data from the strategy.
        /// </summary>
        void Clear();
    }
}