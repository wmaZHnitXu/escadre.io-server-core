// File: Core/Client/ClientEntityManager.cs
using System;
using System.IO;
using Core.Network;
using Core.Network.Proxies;
using Core.Logging;
using Core.Model; // For Entity.EntityTypeEnum
using Core.Time;    // For IClock

namespace Core.Client 
{
    public class ClientEntityManager : IDisposable
    {
        private readonly IClientNetworkLayer _networkLayer;
        private readonly ClientLevel _clientLevel; 
        private readonly IClock _clock; // Added for creating ClientLevel

        // Constructor updated to accept IClock or create a default one
        public ClientEntityManager(IClientNetworkLayer networkLayer, ClientLevel clientLevel, IClock clock)
        {
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _clientLevel = clientLevel ?? throw new ArgumentNullException(nameof(clientLevel)); // ClientLevel is now injected
            _clock = clock ?? throw new ArgumentNullException(nameof(clock)); // Clock is now injected

            _networkLayer.OnMessageReceived += HandleServerMessage;
            Logger.Log("[ClientEntityManager] Initialized and subscribed to network messages.");
        }

        // Overload for convenience if ClientLevel is created internally (less common with DI)
        public ClientEntityManager(IClientNetworkLayer networkLayer, IClock clock)
        {
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _clientLevel = new ClientLevel(_clock); // Create ClientLevel internally

            _networkLayer.OnMessageReceived += HandleServerMessage;
            Logger.Log("[ClientEntityManager] Initialized (created own ClientLevel) and subscribed to network messages.");
        }


        private void HandleServerMessage(int entityId, MessageType messageType, BinaryReader reader)
        {
            // Logger.Log($"[ClientEntityManager] Received Server Message: EntityID={entityId}, Type={messageType}");

            if (messageType == MessageType.CreateEntity)
            {
                if (_clientLevel.TryGetProxy(entityId, out IClientProxy oldProxy))
                {
                    Logger.LogWarning($"[ClientEntityManager] Received CreateEntity for already existing proxy ID: {entityId} (Type: {oldProxy.EntityType}). Will be replaced.");
                }

                try
                {
                    Entity.EntityTypeEnum entityType = (Entity.EntityTypeEnum)reader.ReadByte();
                    // Pass ClientLevel to the factory method
                    IClientProxy newProxy = ClientProxyFactory.CreateClientProxy(entityId, entityType, _clientLevel, reader); 

                    // Subscribe to proxy lifecycle events
                    newProxy.OnLoudDestructionSignaled += () => HandleProxyLoudDestruction(newProxy);
                    newProxy.OnDestroyed += () => HandleProxyVanished(newProxy); 

                    if (!_clientLevel.AddProxy(newProxy)) 
                    {
                        Logger.LogError($"[ClientEntityManager] Failed to add new proxy {entityId} (Type: {entityType}) to ClientLevel.");
                        newProxy.OnLoudDestructionSignaled -= () => HandleProxyLoudDestruction(newProxy);
                        newProxy.OnDestroyed -= () => HandleProxyVanished(newProxy);
                    } else {
                         Logger.Log($"[ClientEntityManager] Processed CreateEntity for EntityID={entityId}, Type={newProxy.EntityType}. Added to ClientLevel.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[ClientEntityManager] Error creating proxy for EntityID={entityId}: {ex.Message}\n{ex.StackTrace}");
                }
            }
            else 
            {
                if (_clientLevel.TryGetProxy(entityId, out IClientProxy proxy))
                {
                    proxy.HandleNetworkMessage(messageType, reader);
                }
                else
                {
                    if (messageType != MessageType.VanishEntity && messageType != MessageType.DestroyEntity)
                    {
                        long payloadLength = 0;
                        if(reader != null && reader.BaseStream != null) payloadLength = reader.BaseStream.Length - reader.BaseStream.Position;
                        Logger.LogWarning($"[ClientEntityManager] Received message Type={messageType} for unknown/destroyed EntityID={entityId}. PayloadLength={payloadLength}. Ignoring.");
                    }
                }
            }
        }

        private void HandleProxyLoudDestruction(IClientProxy proxy)
        {
            Logger.Log($"[ClientEntityManager] Proxy Event: LoudDestructionSignaled for EntityID={proxy.EntityId}, Type={proxy.EntityType}. (Client view should play effects)");
        }

        private void HandleProxyVanished(IClientProxy proxy) 
        {
            Logger.Log($"[ClientEntityManager] Proxy Event: OnDestroyed (vanished) for EntityID={proxy.EntityId}, Type={proxy.EntityType}. Removing from ClientLevel.");
            if (!_clientLevel.RemoveProxy(proxy.EntityId, out _))
            {
                Logger.LogWarning($"[ClientEntityManager] HandleProxyVanished: Proxy {proxy.EntityId} was already removed from ClientLevel or not found.");
            }
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
                foreach(var proxy in _clientLevel.GetAllProxies())
                {
                    proxy.OnLoudDestructionSignaled -= () => HandleProxyLoudDestruction(proxy);
                    proxy.OnDestroyed -= () => HandleProxyVanished(proxy);
                    proxy.NotifyDestroyed();
                }
                _clientLevel.Dispose(); 
            }
            Logger.Log("[ClientEntityManager] Disposed.");
        }
    }
}