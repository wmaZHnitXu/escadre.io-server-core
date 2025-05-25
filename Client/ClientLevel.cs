// File: Core/Client/ClientLevel.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Network; // For IClientProxy
using Core.Logging;
using Core.Time;    // For IClock
using Core.Ocean;   

namespace Core.Client
{
    public class ClientLevel : IDisposable
    {
        private readonly Dictionary<int, IClientProxy> _activeProxies = new Dictionary<int, IClientProxy>();
        public IReadOnlyDictionary<int, IClientProxy> ActiveProxies => _activeProxies;

        public event Action<IClientProxy> OnProxyAdded;
        public event Action<IClientProxy> OnProxyRemoved;
        public event Action<OceanSettings> OnOceanSettingsReceived; 

        private readonly IClock _clock;
        public float CurrentTime => _clock.CurrentTime;

        public IOceanDataProvider OceanDataProvider { get; private set; }
        public bool IsOceanInitialized => OceanDataProvider != null && OceanDataProvider.Settings != null;


        public ClientLevel(IClock clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Logger.Log("[ClientLevel] Initialized. Waiting for ocean data from server.");
        }
        
        /// <summary>
        /// Initializes the ocean data provider for the client level.
        /// This is typically called after OceanSettings are received from the server.
        /// </summary>
        public void InitializeOcean(IOceanDataProvider dataProvider) 
        {
            if (OceanDataProvider != null)
            {
                Logger.LogWarning("[ClientLevel] OceanDataProvider is already initialized. Replacing.");
                (OceanDataProvider as IDisposable)?.Dispose();
            }
            OceanDataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
            OceanSettings settings = OceanDataProvider.Settings; 
            Logger.Log($"[ClientLevel] Ocean data provider initialized. TileSize: {settings.TextureTileWorldSize}, LoopDur: {settings.TextureTimeLoopDuration}, Scale: {settings.DisplacementScale}");
        }

        /// <summary>
        /// Internal method called by ClientEntityManager when OceanSettings are received from the server.
        /// This fires an event that ClientComposer can use to construct the IOceanDataProvider with local raw data.
        /// </summary>
        internal void TriggerOceanSettingsReceived(OceanSettings settings)
        {
            if (settings == null)
            {
                Logger.LogError("[ClientLevel] TriggerOceanSettingsReceived called with null settings.");
                return;
            }
            OnOceanSettingsReceived?.Invoke(settings);
        }


        public void DoUpdate(float deltaTime)
        {
            var proxiesToUpdate = _activeProxies.Values.ToList();
            foreach (var proxy in proxiesToUpdate)
            {
                if (!_activeProxies.ContainsKey(proxy.EntityId)) continue;
                try
                {
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
                    RemoveProxy(oldProxy.EntityId, out _); 
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
            OnOceanSettingsReceived = null; 
            (OceanDataProvider as IDisposable)?.Dispose();
            OceanDataProvider = null; 
            Logger.Log("[ClientLevel] Disposed."); 
        }
    }
}