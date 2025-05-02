// File: Scripts/Server/Core/Network/Proxies/BaseServerProxy.cs
using System;
using System.IO;
using Server.Core.Model;
using Server.Core.Primitives;
using Core.Network;
using Core.Logging;

namespace Core.Network.Proxies
{
    public abstract class BaseServerProxy<TEntity> : IServerProxy where TEntity : Entity
    {
        protected readonly TEntity _entity;
        protected readonly IServerNetworkLayer _networkLayer; // Still needed for sending Events if any
        public int EntityId => _entity.Id;
        protected virtual float ChecksumThreshold => 0.01f;

        protected BaseServerProxy(TEntity entity, IServerNetworkLayer networkLayer)
        {
            _entity = entity ?? throw new ArgumentNullException(nameof(entity));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _entity.OnDeathEvent += HandleEntityDeath;
        }

        public virtual void StartReplicating()
        {
            // Sending Create message remains broadcast
            _networkLayer.BroadcastRelevant(EntityId, MessageType.CreateEntity, writer =>
            {
                writer.Write((byte)_entity.EntityType);
                SerializationUtils.WriteVector3(writer, _entity.Position);
                SerializationUtils.WriteQuaternion(writer, _entity.Rotation);
                SerializeInitialState(writer);
            });
            StartReplicatingInternal();
        }

        public virtual void StopReplicating()
        {
            _entity.OnDeathEvent -= HandleEntityDeath;
            StopReplicatingInternal();
        }

        public void SendDestroyMessage()
        {
            // Sending Destroy message remains broadcast (or could be targeted)
            _networkLayer.SendDestroyCommand(EntityId);
        }

        /// <summary>
        /// Checks the checksum received from the client against the server's current state.
        /// Returns true if the difference exceeds the threshold and a correction is needed.
        /// </summary>
        /// <param name="reader">Reader positioned at the client's checksum.</param>
        /// <returns>True if an UpdateState correction should be sent.</returns>
        public bool CheckClientSyncState(BinaryReader reader)
        {
            if (_entity.IsDead)
            {
                // Logger.LogWarning($"[BaseServerProxy {EntityId}] CheckClientSyncState called for dead entity.");
                return false; // No need to correct a dead entity
            }

            try
            {
                float clientChecksum = reader.ReadSingle();
                float serverChecksum = CalculateChecksum();
                float difference = Math.Abs(serverChecksum - clientChecksum);

                // Logger.Log($"[BaseServerProxy {EntityId}] Checking ClientSync. Client Oni: {clientChecksum}, Server Oni: {serverChecksum}, Diff: {difference}"); // DEBUG

                return difference > ChecksumThreshold;
            }
            catch (EndOfStreamException eof) { Logger.LogError($"[BaseServerProxy {EntityId}] Error reading client checksum (End of Stream): {eof.Message}"); }
            catch (Exception ex) { Logger.LogError($"[BaseServerProxy {EntityId}] Error checking ClientSyncState: {ex.Message}\nStackTrace: {ex.StackTrace}"); }

            return false; // Don't send correction on error
        }

        /// <summary>
        /// Writes the full authoritative state required for an UpdateState correction message.
        /// </summary>
        /// <param name="writer">The writer to serialize state into.</param>
        public void SerializeCorrectionState(BinaryWriter writer)
        {
            // Write common state
            SerializationUtils.WriteVector3(writer, _entity.Position);
            SerializationUtils.WriteQuaternion(writer, _entity.Rotation);
            // Derived class writes specific state
            SerializeState(writer);
        }


        // SendEvent still uses BroadcastRelevant
        protected void SendEvent(byte specificEventType, Action<BinaryWriter> serializeEventPayloadAction)
        {
            _networkLayer.BroadcastRelevant(EntityId, MessageType.EntityEvent, writer =>
            {
                writer.Write(specificEventType);
                serializeEventPayloadAction?.Invoke(writer);
            });
        }

        private void HandleEntityDeath(Entity deadEntity)
        {
            StopReplicating();
        }

        // --- Abstract methods ---
        protected abstract float CalculateChecksum();
        protected abstract void SerializeInitialState(BinaryWriter writer);
        /// <summary> Used for SerializeCorrectionState </summary>
        protected abstract void SerializeState(BinaryWriter writer);
        protected virtual void StartReplicatingInternal() { }
        protected virtual void StopReplicatingInternal() { }
    }

    // --- Base Client Proxy (No changes needed) ---
    public abstract class BaseClientProxy : IClientProxy
    { /* ... Same as before ... */
        public int EntityId { get; }
        public abstract Entity.EntityTypeEnum EntityType { get; }
        protected Vector3 _position; protected Quaternion _rotation; protected bool _isDestroyed = false; public Vector3 Position => _position; public Quaternion Rotation => _rotation; public event Action OnDestroyed; public event Action<Vector3> PositionChanged; public event Action<Quaternion> RotationChanged;
        protected BaseClientProxy(int entityId) { EntityId = entityId; }
        public void HandleNetworkMessage(MessageType messageType, BinaryReader reader) { if (_isDestroyed) return; try { switch (messageType) { case MessageType.UpdateState: DeserializeAndUpdateState(reader); break; case MessageType.EntityEvent: DeserializeAndDispatchEvent(reader); break; default: Logger.LogWarning($"[ClientProxy {EntityId}] Received unhandled message type: {messageType}"); break; } } catch (Exception ex) { Logger.LogError($"[ClientProxy {EntityId}] Error processing message {messageType}: {ex.Message} \nStackTrace: {ex.StackTrace}"); } }
        public void NotifyDestroyed() { if (_isDestroyed) return; _isDestroyed = true; OnDestroyed?.Invoke(); CleanupEvents(); }
        protected virtual void DeserializeAndUpdateState(BinaryReader reader) { var oldPos = _position; var oldRot = _rotation; _position = SerializationUtils.ReadVector3(reader); _rotation = SerializationUtils.ReadQuaternion(reader); DeserializeSpecificState(reader); if (_position != oldPos) PositionChanged?.Invoke(_position); if (_rotation != oldRot) RotationChanged?.Invoke(_rotation); InvokeSpecificStateChangedEvents(); }
        protected virtual void DeserializeAndDispatchEvent(BinaryReader reader) { byte specificEventType = reader.ReadByte(); HandleSpecificEvent(specificEventType, reader); }
        public virtual void Initialize(BinaryReader reader) { _position = SerializationUtils.ReadVector3(reader); _rotation = SerializationUtils.ReadQuaternion(reader); DeserializeSpecificInitialState(reader); }
        protected virtual void CleanupEvents() { OnDestroyed = null; PositionChanged = null; RotationChanged = null; }
        protected abstract void DeserializeSpecificInitialState(BinaryReader reader); protected abstract void DeserializeSpecificState(BinaryReader reader); protected abstract void HandleSpecificEvent(byte specificEventType, BinaryReader reader); protected abstract void InvokeSpecificStateChangedEvents();
    }

    // --- Shared Serialization Helpers (No changes needed) ---
    internal static class SerializationUtils
    { /* ... Same as before ... */
        public static void WriteVector3(BinaryWriter writer, Vector3 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
        public static Vector3 ReadVector3(BinaryReader reader) { return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); }
        public static void WriteQuaternion(BinaryWriter writer, Quaternion q) { writer.Write(q.X); writer.Write(q.Y); writer.Write(q.Z); writer.Write(q.W); }
        public static Quaternion ReadQuaternion(BinaryReader reader) { return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); }
    }
}