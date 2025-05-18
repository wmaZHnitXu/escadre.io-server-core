// File: Core/Client/ClientLevel.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Network; // For IClientProxy
using Core.Logging;

namespace Core.Client
{
    /// <summary>
    /// Manages the collection of active client-side entity proxies (IClientProxy).
    /// Analogous to the server-side Level for Entities.
    /// </summary>
    public class ClientLevel : IDisposable
    {
        private readonly Dictionary<int, IClientProxy> _activeProxies = new Dictionary<int, IClientProxy>();
        public IReadOnlyDictionary<int, IClientProxy> ActiveProxies => _activeProxies;

        /// <summary>
        /// Event raised when a new proxy is added to this ClientLevel.
        /// </summary>
        public event Action<IClientProxy> OnProxyAdded;

        /// <summary>
        /// Event raised when a proxy is removed from this ClientLevel.
        /// </summary>
        public event Action<IClientProxy> OnProxyRemoved;

        public ClientLevel()
        {
            Logger.Log("[ClientLevel] Initialized.");
        }

        /// <summary>
        /// Adds a proxy to the collection and raises the OnProxyAdded event.
        /// </summary>
        /// <param name="proxy">The proxy to add.</param>
        /// <returns>True if added successfully, false if proxy is null or ID already exists.</returns>
        public bool AddProxy(IClientProxy proxy)
        {
            if (proxy == null)
            {
                Logger.LogWarning("[ClientLevel] Attempted to add a null proxy.");
                return false;
            }
            if (_activeProxies.ContainsKey(proxy.EntityId))
            {
                Logger.LogWarning($"[ClientLevel] Proxy with ID {proxy.EntityId} (Type: {proxy.EntityType}) already exists. Replacing.");
                // Potentially remove old one first or handle as an error
                if(_activeProxies.TryGetValue(proxy.EntityId, out var oldProxy))
                {
                    RemoveProxy(oldProxy.EntityId, out _); // Trigger removal for the old one
                }
            }

            _activeProxies[proxy.EntityId] = proxy;
            Logger.Log($"[ClientLevel] Added Proxy: ID={proxy.EntityId}, Type={proxy.EntityType}. Total proxies: {_activeProxies.Count}");
            OnProxyAdded?.Invoke(proxy);
            return true;
        }

        /// <summary>
        /// Removes a proxy from the collection by its entity ID and raises the OnProxyRemoved event.
        /// </summary>
        /// <param name="entityId">The ID of the entity whose proxy is to be removed.</param>
        /// <param name="removedProxy">The proxy that was removed, or null if not found.</param>
        /// <returns>True if the proxy was found and removed, false otherwise.</returns>
        public bool RemoveProxy(int entityId, out IClientProxy removedProxy)
        {
            if (_activeProxies.TryGetValue(entityId, out removedProxy))
            {
                _activeProxies.Remove(entityId);
                Logger.Log($"[ClientLevel] Removed Proxy: ID={entityId}, Type={removedProxy.EntityType}. Total proxies: {_activeProxies.Count}");
                OnProxyRemoved?.Invoke(removedProxy);
                return true;
            }
            removedProxy = null;
            // Logger.LogWarning($"[ClientLevel] Attempted to remove non-existent proxy with ID {entityId}.");
            return false;
        }

        /// <summary>
        /// Tries to get an active proxy by its entity ID.
        /// </summary>
        public bool TryGetProxy(int entityId, out IClientProxy proxy)
        {
            return _activeProxies.TryGetValue(entityId, out proxy);
        }

        /// <summary>
        /// Gets an enumeration of all active proxies.
        /// </summary>
        public IEnumerable<IClientProxy> GetAllProxies()
        {
            return _activeProxies.Values;
        }

        /// <summary>
        /// Clears all proxies from the ClientLevel, typically on shutdown or disconnect.
        /// Raises OnProxyRemoved for each proxy.
        /// </summary>
        public void ClearAllProxies()
        {
            Logger.Log($"[ClientLevel] Clearing all {_activeProxies.Count} proxies.");
            // Iterate a copy for safe removal and event invocation
            foreach (var proxy in _activeProxies.Values.ToList())
            {
                RemoveProxy(proxy.EntityId, out _);
                // Note: If proxies need specific cleanup via their own methods before removal,
                // that logic should be triggered elsewhere (e.g., by ClientEntityManager
                // ensuring proxy.NotifyDestroyed() has been called, which ClientLevel doesn't know about directly).
            }
            _activeProxies.Clear(); // Should be empty already if RemoveProxy worked
        }

        public void Dispose()
        {
            ClearAllProxies();
            OnProxyAdded = null;
            OnProxyRemoved = null;
            Logger.Log("[ClientLevel] Disposed.");
        }
    }
}