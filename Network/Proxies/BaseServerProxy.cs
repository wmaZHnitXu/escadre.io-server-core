// File: Scripts/Server/Core/Network/Proxies/BaseServerProxy.cs
using System;
using System.IO;
using Core.Model; // For Entity
using Core.Primitives; // For Vector3, Quaternion
using Core.Network; // For interfaces and types
using Core.Logging;
using System.Collections.Generic;
using System.Linq; // Use Logger

namespace Core.Network.Proxies
{
    /// <summary>
    /// Base class for server-side entity proxies. Handles common logic like
    /// lifecycle events, checksum checking, and delegating specific serialization
    /// and event handling to derived classes.
    /// </summary>
    /// <typeparam name="TEntity">The type of the Core Entity being proxied.</typeparam>
    public abstract class BaseServerProxy<TEntity> : IServerProxy where TEntity : Entity
    {
        protected readonly TEntity _entity;
        protected readonly IServerNetworkLayer _networkLayer;
        protected virtual float ChecksumThreshold => 0.01f;

        // --- IServerProxy Implementation ---
        public int EntityId => _entity.Id;
        public Entity.EntityTypeEnum EntityType => _entity.EntityType; // Implement property
        public Vector3 Position => _entity.Position;         // Implement property
        public Quaternion Rotation => _entity.Rotation;        // Implement property

        protected BaseServerProxy(TEntity entity, IServerNetworkLayer networkLayer)
        {
            _entity = entity ?? throw new ArgumentNullException(nameof(entity));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            // NOTE: We subscribe to OnDeathEvent here in the constructor
            // but unsubscribe in StopReplicating(). This ensures StopReplicating is called.
            // The ServerReplicationManager also calls StopReplicating()
            // when the entity is no longer visible to anyone or when it dies.
            _entity.OnDeathEvent += HandleEntityDeath;
        }

        // Problem 1 fix: This public method is now called by ServerReplicationManager
        // when the entity becomes visible to the first client.
        public virtual void StartReplicating()
        {
            Logger.Log($"[BaseServerProxy {EntityId}] Started replicating (subscribing to events).");
            StartReplicatingInternal(); // Calls derived proxy's subscription logic
        }

        // Problem 1 fix: This public method is called by ServerReplicationManager
        // when the entity is no longer visible to any client OR when it dies.
        public virtual void StopReplicating()
        {
            Logger.Log($"[BaseServerProxy {EntityId}] Stopping replicating (unsubscribing from events).");
            // Important: Unsubscribe from entity events within StopReplicatingInternal
            StopReplicatingInternal(); // Calls derived proxy's unsubscription logic

            // Also unsubscribe from the death event if it hasn't fired yet
            // This ensures we don't try to stop replication twice if death triggers first
             _entity.OnDeathEvent -= HandleEntityDeath;
        }

        // This method is called by the entity itself upon death
        private void HandleEntityDeath(Entity deadEntity)
        {
             // The entity died, stop replication and let the manager clean up
             // We don't send the destroy message here directly, ServerReplicationManager does it
             // after getting the list of seeing clients *before* unregistering visibility.
             // Just ensure StopReplicating is called.
            StopReplicating();
            // ServerReplicationManager's HandleEntityDeath will finish the process
            // (unregister visibility, remove proxy from manager's collections, send destroy message).
        }


        // Problem 2 fix part: Implemented SendDestroyMessage to use network layer's command
        public void SendDestroyMessage(IEnumerable<int> targetClientIds) // Implement interface method
        {
             if (targetClientIds == null || !targetClientIds.Any()) return;
             // Delegates the actual sending logic to the network layer,
             // which now correctly uses targetClientIds in the mock (Problem 2 fix).
            _networkLayer.SendDestroyCommand(EntityId, targetClientIds);
        }

        public bool CheckClientSyncState(BinaryReader reader)
        {
             // Same implementation as before
             if (_entity.IsDead) return false;
             try { float clientChecksum = reader.ReadSingle(); float serverChecksum = CalculateChecksum(); float difference = Math.Abs(serverChecksum - clientChecksum); bool needsCorrection = difference > ChecksumThreshold; if (needsCorrection) { Logger.Log($"[BaseServerProxy {EntityId}] Checksum difference ({difference}) exceeds threshold ({ChecksumThreshold}). Correction required. Server: {serverChecksum}, Client: {clientChecksum}"); } return needsCorrection; }
             catch (EndOfStreamException eof) { Logger.LogError($"[BaseServerProxy {EntityId}] Error reading client checksum (End of Stream): {eof.Message}"); }
             catch (Exception ex) { Logger.LogError($"[BaseServerProxy {EntityId}] Error checking ClientSyncState: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
             return false;
        }

        // Abstract method now matches interface name
        public abstract void SerializeSpecificInitialState(BinaryWriter writer);

        public void SerializeCorrectionState(BinaryWriter writer)
        {
            // Common state
            SerializationUtils.WriteVector3(writer, _entity.Position);
            SerializationUtils.WriteQuaternion(writer, _entity.Rotation);
            // Specific state for correction (might differ from initial)
            SerializeSpecificCorrectionState(writer);
        }

        // SendEvent remains the same, but now calls BroadcastRelevant
        // which is fixed in the MockNetworkLayer (Problem 2 fix part)
        protected void SendEvent(byte specificEventType, Action<BinaryWriter> serializeEventPayloadAction)
        {
             // BroadcastRelevant will use the VisibilityManager (via the Network Layer mock)
             // to determine which clients should receive this event.
             _networkLayer.BroadcastRelevant(EntityId, MessageType.EntityEvent, writer =>
             {
                 writer.Write(specificEventType); // Write the specific event type first
                 serializeEventPayloadAction?.Invoke(writer); // Then write the event-specific payload
             });
         }

        // --- Abstract methods for derived classes ---
        protected abstract float CalculateChecksum();
        /// <summary> Serializes specific state for UpdateState correction message. </summary>
        protected abstract void SerializeSpecificCorrectionState(BinaryWriter writer);

        /// <summary> Called when replication starts. Derived classes should subscribe to entity events here. </summary>
        protected virtual void StartReplicatingInternal() { }

        /// <summary> Called when replication stops. Derived classes should unsubscribe from entity events here. </summary>
        protected virtual void StopReplicatingInternal() { }
    }

    // --- Base Client Proxy (Remains unchanged from previous correct version) ---
    public abstract class BaseClientProxy : IClientProxy {
        public int EntityId { get; } public abstract Entity.EntityTypeEnum EntityType { get; } protected Vector3 _position; protected Quaternion _rotation; protected bool _isDestroyed = false; public Vector3 Position => _position; public Quaternion Rotation => _rotation; public event Action OnDestroyed; public event Action<Vector3> PositionChanged; public event Action<Quaternion> RotationChanged;
        protected BaseClientProxy(int entityId) { EntityId = entityId; }
        public void HandleNetworkMessage(MessageType messageType, BinaryReader reader) { if (_isDestroyed) return; try { switch (messageType) { case MessageType.UpdateState: DeserializeAndUpdateState(reader); break; case MessageType.EntityEvent: DeserializeAndDispatchEvent(reader); break; default: Logger.LogWarning($"[ClientProxy {EntityId}] Received unhandled message type: {messageType}"); break; } } catch (Exception ex) { Logger.LogError($"[ClientProxy {EntityId}] Error processing message {messageType}: {ex.Message} \nStackTrace: {ex.StackTrace}"); } }
        public void NotifyDestroyed() { if (_isDestroyed) return; _isDestroyed = true; OnDestroyed?.Invoke(); CleanupEvents(); }
        // Initialize reads Type (externally), then Pos/Rot and Specific Initial State
        public virtual void Initialize(BinaryReader reader) { _position = SerializationUtils.ReadVector3(reader); _rotation = SerializationUtils.ReadQuaternion(reader); DeserializeSpecificInitialState(reader); }
        protected virtual void DeserializeAndUpdateState(BinaryReader reader) { var oldPos = _position; var oldRot = _rotation; _position = SerializationUtils.ReadVector3(reader); _rotation = SerializationUtils.ReadQuaternion(reader); DeserializeSpecificState(reader); if (_position != oldPos) PositionChanged?.Invoke(_position); if (_rotation != oldRot) RotationChanged?.Invoke(_rotation); InvokeSpecificStateChangedEvents(); }
        protected virtual void DeserializeAndDispatchEvent(BinaryReader reader) { byte specificEventType = reader.ReadByte(); HandleSpecificEvent(specificEventType, reader); }
        protected virtual void CleanupEvents() { OnDestroyed = null; PositionChanged = null; RotationChanged = null; }
        protected abstract void DeserializeSpecificInitialState(BinaryReader reader); protected abstract void DeserializeSpecificState(BinaryReader reader); protected abstract void HandleSpecificEvent(byte specificEventType, BinaryReader reader); protected abstract void InvokeSpecificStateChangedEvents();
     }

    // --- Shared Serialization Helpers (Remains unchanged) ---
    internal static class SerializationUtils {
        public static void WriteVector3(BinaryWriter writer, Vector3 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); } public static Vector3 ReadVector3(BinaryReader reader) { return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); } public static void WriteQuaternion(BinaryWriter writer, Quaternion q) { writer.Write(q.X); writer.Write(q.Y); writer.Write(q.Z); writer.Write(q.W); } public static Quaternion ReadQuaternion(BinaryReader reader) { return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); }
    }
}