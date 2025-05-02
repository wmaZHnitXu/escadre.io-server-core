// File: Scripts/Server/Core/Network/ServerReplicationManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using Server.Core.Model;
using Core.Network;
using Core.Network.Proxies;
using Core.Logging;

namespace Core.Network
{
    public class ServerReplicationManager : IDisposable
    {
        private readonly Level _level;
        private readonly IServerNetworkLayer _networkLayer;
        private readonly Dictionary<int, IServerProxy> _activeProxies = new ();
        private bool _isDisposed = false;

        public ServerReplicationManager(Level level, IServerNetworkLayer networkLayer)
        {
            _level = level ?? throw new ArgumentNullException(nameof(level));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _level.OnEntityAddedEvent += HandleEntityAdded;
            _networkLayer.OnClientMessageReceived += HandleClientMessage; // Now includes clientId
            Logger.Log("[ServerReplicationManager] Initialized. Subscribed to Level & Network Layer events.");
        }

        // HandleEntityAdded remains the same
        private void HandleEntityAdded(Entity entity) { if (_isDisposed || entity.IsDead || _activeProxies.ContainsKey(entity.Id)) return; Logger.Log($"[ServerReplicationManager] Entity Added: ID={entity.Id}, Type={entity.EntityType}. Creating Proxy."); try { IServerProxy proxy = ServerProxyFactory.CreateServerProxy(entity, _networkLayer); _activeProxies.Add(entity.Id, proxy); entity.OnDeathEvent += HandleEntityDeath; proxy.StartReplicating(); } catch(Exception ex) { Logger.LogError($"[ServerReplicationManager] Error creating proxy for Entity {entity.Id} ({entity.EntityType}): {ex.Message}\nStackTrace: {ex.StackTrace}"); } }
        // HandleEntityDeath remains the same
        private void HandleEntityDeath(Entity entity) { if (_isDisposed) return; if (_activeProxies.TryGetValue(entity.Id, out IServerProxy proxy)) { Logger.Log($"[ServerReplicationManager] Entity Died: ID={entity.Id}. Stopping Proxy & Sending Destroy."); entity.OnDeathEvent -= HandleEntityDeath; proxy.StopReplicating(); proxy.SendDestroyMessage(); _activeProxies.Remove(entity.Id); } else { Logger.LogWarning($"[ServerReplicationManager] HandleEntityDeath called for Entity ID={entity.Id}, but no active proxy found."); entity.OnDeathEvent -= HandleEntityDeath; } }


        /// <summary>
        /// Handles incoming messages from clients, received via the network layer event.
        /// Now includes the clientId.
        /// </summary>
        private void HandleClientMessage(int clientId, int entityId, MessageType messageType, BinaryReader payloadReader) // Added clientId
        {
             if (_isDisposed) return;

            // Logger.Log($"[ServerReplicationManager] Received Client Msg From CId={clientId}: Entity={entityId}, Type={messageType}"); // More detailed log

            if (_activeProxies.TryGetValue(entityId, out IServerProxy proxy))
            {
                 switch(messageType)
                 {
                     case MessageType.ClientSyncState:
                         // Check if correction is needed
                         bool needsCorrection = proxy.CheckClientSyncState(payloadReader);
                         if (needsCorrection)
                         {
                             // Send correction via UNICAST using the new network layer method
                             Logger.Log($"[ServerReplicationManager] Sending UpdateState correction to ClientId={clientId} for Entity={entityId}.");
                             _networkLayer.SendToClient(clientId, entityId, MessageType.UpdateState, writer =>
                             {
                                 // Proxy provides the state data
                                 proxy.SerializeCorrectionState(writer);
                             });
                         }
                         break;

                     // Handle other C->S messages
                     default:
                         Logger.LogWarning($"[ServerReplicationManager] Received unhandled MessageType ({messageType}) from ClientId={clientId} for Entity {entityId}.");
                         break;
                 }
            }
            else { Logger.LogWarning($"[ServerReplicationManager] Received message ({messageType}) for unknown/inactive Entity ID: {entityId} from ClientId={clientId}."); }
        }


        /// <summary>
        /// Update loop still only updates the Level model.
        /// </summary>
        public void UpdateCoreModel(float delta)
        {
             if (_isDisposed) return;
             _level.DoUpdate(delta);
        }

        // Dispose remains the same
        public void Dispose() { if (_isDisposed) return; _isDisposed = true; Logger.Log("[ServerReplicationManager] Disposing..."); if (_networkLayer != null) _networkLayer.OnClientMessageReceived -= HandleClientMessage; _level.OnEntityAddedEvent -= HandleEntityAdded; var proxyIds = new List<int>(_activeProxies.Keys); foreach (var id in proxyIds) { if (_activeProxies.TryGetValue(id, out IServerProxy proxy)) { proxy.StopReplicating(); proxy.SendDestroyMessage(); } } _activeProxies.Clear(); Logger.Log("[ServerReplicationManager] Dispose complete."); }
    }
}