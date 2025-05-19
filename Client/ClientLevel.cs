// File: Core/Client/ClientLevel.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Network; // For IClientProxy
using Core.Logging;
using Core.Time;    // For IClock

namespace Core.Client
{
    public class ClientLevel : IDisposable
    {
        private readonly Dictionary<int, IClientProxy> _activeProxies = new Dictionary<int, IClientProxy>();
        public IReadOnlyDictionary<int, IClientProxy> ActiveProxies => _activeProxies;

        public event Action<IClientProxy> OnProxyAdded;
        public event Action<IClientProxy> OnProxyRemoved;

        private readonly IClock _clock;
        public float CurrentTime => _clock.CurrentTime;

        public ClientLevel(IClock clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Logger.Log("[ClientLevel] Initialized.");
        }

        public void DoUpdate(float deltaTime)
        {
            // The _clock.CurrentTime will reflect the advancing time.
            // deltaTime is the frame's delta.

            // Iterate a copy of values in case an Update call leads to a proxy being removed
            var proxiesToUpdate = _activeProxies.Values.ToList();
            foreach (var proxy in proxiesToUpdate)
            {
                // Ensure proxy wasn't removed mid-iteration by another thread/callback
                if (!_activeProxies.ContainsKey(proxy.EntityId)) continue;

                try
                {
                    // Pass only deltaTime. Proxies can get CurrentTime from this ClientLevel instance.
                    proxy.Update(deltaTime);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[ClientLevel] Error updating proxy {proxy.EntityId} (Type: {proxy.EntityType}): {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        public bool AddProxy(IClientProxy proxy)
        {
            if (proxy == null) {
                Logger.LogWarning("[ClientLevel] Attempted to add a null proxy.");
                return false;
            }
            if (_activeProxies.ContainsKey(proxy.EntityId)) {
                Logger.LogWarning($"[ClientLevel] Proxy ID {proxy.EntityId} (NewType: {proxy.EntityType}, OldType: {_activeProxies[proxy.EntityId].EntityType}) already exists. Replacing.");
                if (_activeProxies.TryGetValue(proxy.EntityId, out var oldProxy)) {
                    // oldProxy.NotifyDestroyed(); // Let ClientEntityManager handle this sequence via HandleProxyVanished
                    RemoveProxy(oldProxy.EntityId, out _); // Ensure it's gone from ClientLevel and event is raised
                }
            }
            _activeProxies[proxy.EntityId] = proxy;
            OnProxyAdded?.Invoke(proxy);
            return true;
        }

        public bool RemoveProxy(int entityId, out IClientProxy removedProxy)
        {
            if (_activeProxies.TryGetValue(entityId, out removedProxy)) {
                _activeProxies.Remove(entityId);
                OnProxyRemoved?.Invoke(removedProxy);
                return true;
            }
            removedProxy = null;
            return false;
        }
        
        public bool TryGetProxy(int entityId, out IClientProxy proxy) 
        { 
            return _activeProxies.TryGetValue(entityId, out proxy); 
        }

        public IEnumerable<IClientProxy> GetAllProxies() 
        { 
            return _activeProxies.Values; 
        }

        public void ClearAllProxies() 
        { 
            Logger.Log($"[ClientLevel] Clearing all {_activeProxies.Count} proxies."); 
            foreach (var proxy in _activeProxies.Values.ToList()) 
            { 
                RemoveProxy(proxy.EntityId, out _); 
            } 
            _activeProxies.Clear(); 
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