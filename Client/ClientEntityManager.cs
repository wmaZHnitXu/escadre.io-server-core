// File: Core/Client/ClientEntityManager.cs
using System;
using System.IO;
using System.Collections.Generic; 
using Core.Network;
using Core.Network.Proxies;
using Core.Logging;
using Core.Model; 
using Core.Time;
using System.Linq;
using Core.Ocean; 

namespace Core.Client 
{
    public class ClientEntityManager : IDisposable
    {
        private readonly IClientNetworkLayer _networkLayer;
        private readonly ClientLevel _clientLevel; 
        private readonly IClock _clock; 

        // _oceanTextureRawDataPlaceholder is no longer needed here.
        // ClientComposer loads the bytes, and ClientLevel holds the IOceanDataProvider.

        public ClientEntityManager(IClientNetworkLayer networkLayer, ClientLevel clientLevel, IClock clock)
        {
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _clientLevel = clientLevel ?? throw new ArgumentNullException(nameof(clientLevel)); 
            _clock = clock ?? throw new ArgumentNullException(nameof(clock)); 

            _networkLayer.OnMessageReceived += HandleServerMessageBytes; 
            Logger.Log("[ClientEntityManager] Initialized and subscribed to network messages (byte[]).");
        }

        public ClientEntityManager(IClientNetworkLayer networkLayer, IClock clock)
            : this(networkLayer, new ClientLevel(clock), clock)
        {
        }
        
        private void HandleServerMessageBytes(int contextId, MessageType messageType, byte[] payload)
        {
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                HandleServerMessage(contextId, messageType, reader); 
            }
        }

        private void HandleServerMessage(int contextId, MessageType messageType, BinaryReader reader)
        {
            switch (messageType)
            {
                case MessageType.CreateEntity: 
                    if (_clientLevel.TryGetProxy(contextId, out IClientProxy oldProxy)) 
                    {
                        Logger.LogWarning($"[ClientEntityManager] Received CreateEntity for already existing proxy ID: {contextId} (Type: {oldProxy.EntityType}). Will be replaced.");
                    }
                    try
                    {
                        Entity.EntityTypeEnum entityType = (Entity.EntityTypeEnum)reader.ReadByte(); 
                        IClientProxy newProxy = ClientProxyFactory.CreateClientProxy(contextId, entityType, _clientLevel, reader); 

                        newProxy.OnLoudDestructionSignaled += () => HandleProxyLoudDestruction(newProxy);
                        newProxy.OnDestroyed += () => HandleProxyDestroyed(newProxy); 

                        if (!_clientLevel.AddProxy(newProxy)) 
                        {
                            Logger.LogError($"[ClientEntityManager] Failed to add new proxy {contextId} (Type: {entityType}) to ClientLevel.");
                            newProxy.OnLoudDestructionSignaled -= () => HandleProxyLoudDestruction(newProxy);
                            newProxy.OnDestroyed -= () => HandleProxyDestroyed(newProxy);
                        } else {
                             Logger.Log($"[ClientEntityManager] Processed CreateEntity for EntityID={contextId}, Type={newProxy.EntityType}. Added to ClientLevel.");
                        }
                    }
                    catch (EndOfStreamException eofEx)
                    {
                        Logger.LogError($"[ClientEntityManager] EndOfStreamException creating proxy for EntityID={contextId}, Type={messageType}: {eofEx.Message}\n{eofEx.StackTrace}. Payload length: {reader.BaseStream.Length}, Position: {reader.BaseStream.Position}");
                    }
                    catch (Exception ex) { Logger.LogError($"[ClientEntityManager] Error creating proxy for EntityID={contextId}, Type={messageType}: {ex.Message}\n{ex.StackTrace}"); }
                    break;

                case MessageType.OceanInitializationData:
                    Logger.Log("[ClientEntityManager] Received OceanInitializationData.");
                    OceanSettings settings = SerializationUtils.ReadOceanSettings(reader);
                    bool hasTextureDataBlock = reader.ReadBoolean(); 
                    if (hasTextureDataBlock)
                    {
                        Logger.LogWarning("[ClientEntityManager] Ocean texture data block indicated in message, but client-side network deserialization of raw texture bytes is not implemented. Client will use its locally loaded texture if available.");
                        // Placeholder for reading raw bytes if they were sent:
                        // int expectedSize = settings.TextureResolutionTime * settings.TextureResolutionXZ * settings.TextureResolutionXZ * 3;
                        // byte[] receivedTextureBytes = reader.ReadBytes(expectedSize);
                        // For now, we assume client loads its own, and server sends 'false' for hasTextureDataBlock.
                    }

                    // Trigger event in ClientLevel so ClientComposer (or other interested parties) can react
                    // by creating the IOceanDataProvider with locally loaded raw bytes.
                    _clientLevel.TriggerOceanSettingsReceived(settings);
                    break;

                case MessageType.DestroyEntity: 
                case MessageType.VanishEntity:  
                case MessageType.UpdateState:   
                case MessageType.EntityEvent:   
                    if (_clientLevel.TryGetProxy(contextId, out IClientProxy proxy)) 
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
                default:
                    Logger.LogWarning($"[ClientEntityManager] Received unhandled server message type: {messageType} for context {contextId}");
                    break;
            }
        }

        private void HandleProxyLoudDestruction(IClientProxy proxy)
        {
            Logger.Log($"[ClientEntityManager] Proxy Event: LoudDestructionSignaled for EntityID={proxy.EntityId}, Type={proxy.EntityType}.");
        }

        private void HandleProxyDestroyed(IClientProxy proxy) 
        {
            Logger.Log($"[ClientEntityManager] Proxy Event: OnDestroyed for EntityID={proxy.EntityId}, Type={proxy.EntityType}. Removing from ClientLevel.");
            if (!_clientLevel.RemoveProxy(proxy.EntityId, out _))
            {
                Logger.LogWarning($"[ClientEntityManager] HandleProxyDestroyed: Proxy {proxy.EntityId} was already removed from ClientLevel or not found.");
            }
            proxy.OnLoudDestructionSignaled -= () => HandleProxyLoudDestruction(proxy);
            proxy.OnDestroyed -= () => HandleProxyDestroyed(proxy);
        }

        public void Dispose()
        {
            if (_networkLayer != null)
            {
                _networkLayer.OnMessageReceived -= HandleServerMessageBytes; 
            }

            if (_clientLevel != null)
            {
                foreach(var proxy in _clientLevel.GetAllProxies().ToList()) 
                {
                    proxy.OnLoudDestructionSignaled -= () => HandleProxyLoudDestruction(proxy);
                    proxy.OnDestroyed -= () => HandleProxyDestroyed(proxy);
                    proxy.NotifyDestroyed(); 
                }
                _clientLevel.Dispose(); 
            }
            Logger.Log("[ClientEntityManager] Disposed.");
        }
    }
}