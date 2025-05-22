// File: Core/Client/ClientEntityManager.cs
using System;
using System.IO;
using System.Collections.Generic; // Required for Dictionary
using Core.Network;
using Core.Network.Proxies;
using Core.Logging;
using Core.Model; 
using Core.Time;    

namespace Core.Client 
{
    public class ClientEntityManager : IDisposable
    {
        private readonly IClientNetworkLayer _networkLayer;
        private readonly ClientLevel _clientLevel; 
        private readonly IClock _clock; 

        // For non-entity state proxies like EscadreProxy.ClientProxy
        // Keyed by OwnerId (which for EscadreProxy is the client's own ID)
        private readonly Dictionary<int, EscadreProxy.ClientProxy> _escadreProxies = new Dictionary<int, EscadreProxy.ClientProxy>();

        public ClientEntityManager(IClientNetworkLayer networkLayer, ClientLevel clientLevel, IClock clock)
        {
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _clientLevel = clientLevel ?? throw new ArgumentNullException(nameof(clientLevel)); 
            _clock = clock ?? throw new ArgumentNullException(nameof(clock)); 

            _networkLayer.OnMessageReceived += HandleServerMessage;
            Logger.Log("[ClientEntityManager] Initialized and subscribed to network messages.");
        }

        // Convenience constructor
        public ClientEntityManager(IClientNetworkLayer networkLayer, IClock clock)
            : this(networkLayer, new ClientLevel(clock), clock)
        {
        }

        /// <summary>
        /// Retrieves the EscadreProxy.ClientProxy for a given owner ID (typically the local client's ID).
        /// Creates it if it doesn't exist. This is usually called when the client knows its own ID.
        /// </summary>
        public EscadreProxy.ClientProxy GetOrCreateEscadreProxy(int ownerClientId)
        {
            if (!_escadreProxies.TryGetValue(ownerClientId, out var escadreProxy))
            {
                escadreProxy = new EscadreProxy.ClientProxy(ownerClientId, _clientLevel);
                _escadreProxies.Add(ownerClientId, escadreProxy);
                escadreProxy.OnRemoved += () => _escadreProxies.Remove(ownerClientId); // Cleanup on removal
                Logger.Log($"[ClientEntityManager] Created EscadreProxy for OwnerID {ownerClientId}");
            }
            return escadreProxy;
        }


        private void HandleServerMessage(int contextId, MessageType messageType, BinaryReader reader)
        {
            // Logger.Log($"[ClientEntityManager] Received Server Message: ContextID={contextId}, Type={messageType}");

            switch (messageType)
            {
                // Entity-specific messages
                case MessageType.CreateEntity:
                    if (_clientLevel.TryGetProxy(contextId, out IClientProxy oldProxy)) // contextId is entityId here
                    {
                        Logger.LogWarning($"[ClientEntityManager] Received CreateEntity for already existing proxy ID: {contextId} (Type: {oldProxy.EntityType}). Will be replaced.");
                        // oldProxy should be cleaned up by its OnDestroyed event if server sends Vanish first,
                        // or ClientLevel.AddProxy will handle replacement.
                    }
                    try
                    {
                        Entity.EntityTypeEnum entityType = (Entity.EntityTypeEnum)reader.ReadByte();
                        IClientProxy newProxy = ClientProxyFactory.CreateClientProxy(contextId, entityType, _clientLevel, reader); 

                        newProxy.OnLoudDestructionSignaled += () => HandleProxyLoudDestruction(newProxy);
                        newProxy.OnDestroyed += () => HandleProxyVanished(newProxy); 

                        if (!_clientLevel.AddProxy(newProxy)) 
                        {
                            Logger.LogError($"[ClientEntityManager] Failed to add new proxy {contextId} (Type: {entityType}) to ClientLevel.");
                            newProxy.OnLoudDestructionSignaled -= () => HandleProxyLoudDestruction(newProxy);
                            newProxy.OnDestroyed -= () => HandleProxyVanished(newProxy);
                        } else {
                             Logger.Log($"[ClientEntityManager] Processed CreateEntity for EntityID={contextId}, Type={newProxy.EntityType}. Added to ClientLevel.");
                        }
                    }
                    catch (Exception ex) { Logger.LogError($"[ClientEntityManager] Error creating proxy for EntityID={contextId}: {ex.Message}\n{ex.StackTrace}"); }
                    break;

                case MessageType.DestroyEntity:
                case MessageType.VanishEntity:
                case MessageType.UpdateState:
                case MessageType.EntityEvent:
                    if (_clientLevel.TryGetProxy(contextId, out IClientProxy proxy)) // contextId is entityId
                    {
                        proxy.HandleNetworkMessage(messageType, reader);
                    }
                    else
                    {
                        if (messageType != MessageType.VanishEntity && messageType != MessageType.DestroyEntity)
                        {
                            Logger.LogWarning($"[ClientEntityManager] Received message Type={messageType} for unknown/destroyed EntityID={contextId}. Ignoring.");
                        }
                    }
                    break;

                // Escadre-level messages (contextId is ClientID/OwnerID)
                case MessageType.UpdateEscadreInfo:
                case MessageType.ShopShipDesignsInfo:
                    if (_escadreProxies.TryGetValue(contextId, out var escadreProxy))
                    {
                        escadreProxy.HandleNetworkMessage(messageType, reader);
                    }
                    else
                    {
                        // This might happen if the client hasn't fully initialized its own EscadreProxy yet.
                        // Or if it's info for another client (e.g. spectator mode, not yet implemented fully)
                        Logger.LogWarning($"[ClientEntityManager] Received Escadre/Shop info (Type: {messageType}) for OwnerID {contextId}, but no local proxy found. Creating one.");
                        var newEscProxy = GetOrCreateEscadreProxy(contextId); // Create if not exists
                        newEscProxy.HandleNetworkMessage(messageType, reader);
                    }
                    break;
                
                // Optional: Handle specific command results from server if implemented
                // case MessageType._BuyShipResult: ... break;

                default:
                    Logger.LogWarning($"[ClientEntityManager] Received unhandled server message type: {messageType} for context {contextId}");
                    break;
            }
        }

        private void HandleProxyLoudDestruction(IClientProxy proxy)
        {
            Logger.Log($"[ClientEntityManager] Proxy Event: LoudDestructionSignaled for EntityID={proxy.EntityId}, Type={proxy.EntityType}.");
        }

        private void HandleProxyVanished(IClientProxy proxy) 
        {
            Logger.Log($"[ClientEntityManager] Proxy Event: OnDestroyed (vanished) for EntityID={proxy.EntityId}, Type={proxy.EntityType}. Removing from ClientLevel.");
            if (!_clientLevel.RemoveProxy(proxy.EntityId, out _))
            {
                Logger.LogWarning($"[ClientEntityManager] HandleProxyVanished: Proxy {proxy.EntityId} was already removed from ClientLevel or not found.");
            }
            // Unsubscribe to prevent memory leaks from proxy object if it's pooled or not GC'd immediately
            proxy.OnLoudDestructionSignaled -= () => HandleProxyLoudDestruction(proxy);
            proxy.OnDestroyed -= () => HandleProxyVanished(proxy);
        }

        public void Dispose()
        {
            if (_networkLayer != null)
            {
                _networkLayer.OnMessageReceived -= HandleServerMessage;
            }

            if (_clientLevel != null)
            {
                foreach(var proxy in _clientLevel.GetAllProxies()) // Entity Proxies
                {
                    proxy.OnLoudDestructionSignaled -= () => HandleProxyLoudDestruction(proxy);
                    proxy.OnDestroyed -= () => HandleProxyVanished(proxy);
                    proxy.NotifyDestroyed();
                }
                _clientLevel.Dispose(); 
            }

            foreach(var escadreProxy in new List<EscadreProxy.ClientProxy>(_escadreProxies.Values)) // Escadre Proxies
            {
                escadreProxy.NotifyRemoved(); // This will trigger its OnRemoved event for cleanup
            }
            _escadreProxies.Clear();

            Logger.Log("[ClientEntityManager] Disposed.");
        }
    }
}