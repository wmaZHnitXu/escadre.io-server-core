// File: Scripts/Server/Core/Visibility/IVisibilityStrategy.cs
using System;
using System.Collections.Generic;
using Core.Model; // Entity
using Core.Primitives; // Vector3, RectFloat

namespace Core.Visibility
{
    /// <summary>
    /// Interface for spatial partitioning strategies used by the VisibilityManager.
    /// Also provides general spatial query capabilities.
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
        /// (Primarily for visibility-specific optimizations like FindObservingClients).
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
        /// Queries for entity IDs within a specified rectangular area (2D).
        /// </summary>
        /// <param name="areaBounds">The 2D rectangle to query.</param>
        /// <returns>A collection of entity IDs within the area.</returns>
        IEnumerable<int> QueryRect(RectFloat areaBounds);

        /// <summary>
        /// Queries for entity IDs within a specified circular area (2D).
        /// </summary>
        /// <param name="center">The 2D center of the circle.</param>
        /// <param name="radius">The radius of the circle.</param>
        /// <returns>A collection of entity IDs within the circle.</returns>
        IEnumerable<int> QueryRadius(Vector2 center, float radius);

        /// <summary>
        /// Optional: Clear all data from the strategy.
        /// </summary>
        void Clear();
    }
}