// File: Core/Client/ClientEntityManager.cs (Moved from Scripts/Client)
using System;
using System.IO;
using Core.Network;
using Core.Network.Proxies;
using Core.Logging;
using Core.Model; // For Entity.EntityTypeEnum

namespace Core.Client // Changed namespace
{
    /// <summary>
    /// Handles incoming server messages, creates/manages IClientProxy instances
    /// through a ClientLevel, and routes messages to appropriate proxies.
    /// This class is NOT a MonoBehaviour.
    /// </summary>
    public class ClientEntityManager : IDisposable
    {
        private readonly IClientNetworkLayer _networkLayer;
        private readonly ClientLevel _clientLevel; // Injected dependency

        public ClientEntityManager(IClientNetworkLayer networkLayer, ClientLevel clientLevel)
        {
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _clientLevel = clientLevel ?? throw new ArgumentNullException(nameof(clientLevel));

            _networkLayer.OnMessageReceived += HandleServerMessage;
            Logger.Log("[ClientEntityManager] Initialized and subscribed to network messages.");
        }

        private void HandleServerMessage(int entityId, MessageType messageType, BinaryReader reader)
        {
            // Logger.Log($"[ClientEntityManager] Received Server Message: EntityID={entityId}, Type={messageType}");

            if (messageType == MessageType.CreateEntity)
            {
                if (_clientLevel.TryGetProxy(entityId, out IClientProxy oldProxy))
                {
                    Logger.LogWarning($"[ClientEntityManager] Received CreateEntity for already existing proxy ID: {entityId} (Type: {oldProxy.EntityType}). Will be replaced.");
                    // The old proxy's OnDestroyed should trigger its removal from ClientLevel via HandleProxyVanished.
                    // For robustness, ensure it's explicitly removed if it wasn't already.
                    // However, AddProxy in ClientLevel also handles replacement.
                }

                try
                {
                    Entity.EntityTypeEnum entityType = (Entity.EntityTypeEnum)reader.ReadByte();
                    IClientProxy newProxy = ClientProxyFactory.CreateClientProxy(entityId, entityType, reader); // Initialize is called by factory

                    // Subscribe to proxy lifecycle events
                    newProxy.OnLoudDestructionSignaled += () => HandleProxyLoudDestruction(newProxy);
                    newProxy.OnDestroyed += () => HandleProxyVanished(newProxy); // OnDestroyed is now triggered by VanishEntity

                    if (!_clientLevel.AddProxy(newProxy)) // AddProxy raises OnProxyAdded
                    {
                        Logger.LogError($"[ClientEntityManager] Failed to add new proxy {entityId} (Type: {entityType}) to ClientLevel.");
                        // Clean up subscriptions if add failed
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
            else // For UpdateState, EntityEvent, DestroyEntity (loud), VanishEntity
            {
                if (_clientLevel.TryGetProxy(entityId, out IClientProxy proxy))
                {
                    proxy.HandleNetworkMessage(messageType, reader);
                }
                else
                {
                    if (messageType != MessageType.VanishEntity && messageType != MessageType.DestroyEntity)
                    {
                        // Log for messages other than cleanup messages for potentially already gone proxies
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
            // Presentation layer would subscribe to proxy.OnLoudDestructionSignaled directly or via an event aggregator.
        }

        private void HandleProxyVanished(IClientProxy proxy) // Called when proxy.OnDestroyed fires (after VanishEntity message)
        {
            Logger.Log($"[ClientEntityManager] Proxy Event: OnDestroyed (vanished) for EntityID={proxy.EntityId}, Type={proxy.EntityType}. Removing from ClientLevel.");
            if (!_clientLevel.RemoveProxy(proxy.EntityId, out _))
            {
                Logger.LogWarning($"[ClientEntityManager] HandleProxyVanished: Proxy {proxy.EntityId} was already removed from ClientLevel or not found.");
            }
            // Unsubscribe to prevent memory leaks if this handler was somehow called multiple times (should not happen with proper event handling)
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
                // Unsubscribe from all existing proxy events before clearing,
                // as ClearAllProxies might not trigger OnDestroyed on the proxies themselves if they were already gone.
                foreach(var proxy in _clientLevel.GetAllProxies())
                {
                    proxy.OnLoudDestructionSignaled -= () => HandleProxyLoudDestruction(proxy);
                    proxy.OnDestroyed -= () => HandleProxyVanished(proxy);
                     // Call NotifyDestroyed on each proxy to ensure their internal cleanup and OnDestroyed event invocation,
                     // which then triggers removal from ClientLevel via HandleProxyVanished.
                    proxy.NotifyDestroyed();
                }
                _clientLevel.Dispose(); // This will call ClearAllProxies
            }
            Logger.Log("[ClientEntityManager] Disposed.");
        }
    }
}