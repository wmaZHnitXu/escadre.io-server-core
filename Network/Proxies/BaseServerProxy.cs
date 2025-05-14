// File: Scripts/Server/Core/Network/Proxies/BaseServerProxy.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq; // For Any() in SendDestroyMessage
using Core.Model;
using Core.Primitives;
using Core.Network;
using Core.Logging;

namespace Core.Network.Proxies
{
    public abstract class BaseServerProxy<TEntity> : IServerProxy where TEntity : Entity
    {
        protected readonly TEntity _entity;
        protected readonly IServerNetworkLayer _networkLayer;
        protected virtual float ChecksumThreshold => 0.01f;

        public int EntityId => _entity.Id;
        public Entity.EntityTypeEnum EntityType => _entity.EntityType;
        public Vector3 Position => _entity.Position;
        public Quaternion Rotation => _entity.Rotation;

        protected BaseServerProxy(TEntity entity, IServerNetworkLayer networkLayer)
        {
            _entity = entity ?? throw new ArgumentNullException(nameof(entity));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _entity.OnDeathEvent += HandleEntityDeath;
        }

        public virtual void StartReplicating()
        {
            Logger.Log($"[BaseServerProxy {EntityId}] Started replicating (subscribing to specific events).");
            StartReplicatingInternal();
        }

        public virtual void StopReplicating()
        {
            Logger.Log($"[BaseServerProxy {EntityId}] Stopping replicating (unsubscribing from specific events).");
            StopReplicatingInternal();
            _entity.OnDeathEvent -= HandleEntityDeath; // Ensure unsubscription
        }

        public void SendDestroyMessage(IEnumerable<int> targetClientIds)
        {
            if (targetClientIds == null || !targetClientIds.Any()) return;
            _networkLayer.SendDestroyCommand(EntityId, targetClientIds);
        }

        public bool CheckClientSyncState(BinaryReader reader)
        {
            if (_entity.IsDead) return false;
            try
            {
                float clientChecksum = reader.ReadSingle();
                float serverChecksum = CalculateChecksum();
                float difference = Math.Abs(serverChecksum - clientChecksum);
                bool needsCorrection = difference > ChecksumThreshold;
                if (needsCorrection)
                {
                    Logger.Log($"[BaseServerProxy {EntityId}] Checksum difference ({difference}) exceeds threshold ({ChecksumThreshold}). Correction required. Server: {serverChecksum}, Client: {clientChecksum}");
                }
                return needsCorrection;
            }
            catch (EndOfStreamException eof) { Logger.LogError($"[BaseServerProxy {EntityId}] Error reading client checksum (End of Stream): {eof.Message}"); }
            catch (Exception ex) { Logger.LogError($"[BaseServerProxy {EntityId}] Error checking ClientSyncState: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
            return false;
        }

        public abstract void SerializeSpecificInitialState(BinaryWriter writer);

        public void SerializeCorrectionState(BinaryWriter writer)
        {
            SerializationUtils.WriteVector3(writer, _entity.Position);
            SerializationUtils.WriteQuaternion(writer, _entity.Rotation);
            SerializeSpecificCorrectionState(writer);
        }

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
            StopReplicating(); // Manager will call final SendDestroyMessage
        }

        protected abstract float CalculateChecksum();
        protected abstract void SerializeSpecificCorrectionState(BinaryWriter writer);
        protected virtual void StartReplicatingInternal() { }
        protected virtual void StopReplicatingInternal() { }
    }

    // --- Base Client Proxy ---
    public abstract class BaseClientProxy : IClientProxy
    {
        public int EntityId { get; }
        public abstract Entity.EntityTypeEnum EntityType { get; }
        protected Vector3 _position;
        protected Quaternion _rotation;
        protected bool _isDestroyed = false;
        public Vector3 Position => _position;
        public Quaternion Rotation => _rotation;
        public event Action OnDestroyed;
        public event Action<Vector3> PositionChanged;
        public event Action<Quaternion> RotationChanged;

        protected BaseClientProxy(int entityId) { EntityId = entityId; }

        public void HandleNetworkMessage(MessageType messageType, BinaryReader reader)
        {
            if (_isDestroyed) return;
            try
            {
                switch (messageType)
                {
                    case MessageType.UpdateState:
                        DeserializeAndUpdateState(reader);
                        break;
                    case MessageType.EntityEvent:
                        DeserializeAndDispatchEvent(reader);
                        break;
                    default:
                        Logger.LogWarning($"[ClientProxy {EntityId}] Received unhandled message type: {messageType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[ClientProxy {EntityId}] Error processing message {messageType}: {ex.Message} \nStackTrace: {ex.StackTrace}");
            }
        }

        public void NotifyDestroyed()
        {
            if (_isDestroyed) return;
            _isDestroyed = true;
            OnDestroyed?.Invoke();
            CleanupEvents();
            Logger.Log($"[ClientProxy {EntityId}] Notified Destroyed.");
        }

        public virtual void Initialize(BinaryReader reader)
        {
            // Type is read by factory before this.
            // Common state:
            _position = SerializationUtils.ReadVector3(reader);
            _rotation = SerializationUtils.ReadQuaternion(reader);
            // Specific state:
            DeserializeSpecificInitialState(reader);
            // Logger.Log($"[ClientProxy {EntityId}] Initialized. Pos: {_position}");
        }

        protected virtual void DeserializeAndUpdateState(BinaryReader reader)
        {
            var oldPos = _position;
            var oldRot = _rotation;
            _position = SerializationUtils.ReadVector3(reader);
            _rotation = SerializationUtils.ReadQuaternion(reader);
            DeserializeSpecificState(reader);

            if (_position != oldPos) PositionChanged?.Invoke(_position);
            if (_rotation != oldRot) RotationChanged?.Invoke(_rotation);
            InvokeSpecificStateChangedEvents();
            // Logger.Log($"[ClientProxy {EntityId}] State Updated. Pos: {_position}");
        }

        protected virtual void DeserializeAndDispatchEvent(BinaryReader reader)
        {
            byte specificEventType = reader.ReadByte();
            // Logger.Log($"[ClientProxy {EntityId}] Dispatching specific event type: {specificEventType}");
            HandleSpecificEvent(specificEventType, reader);
        }

        protected virtual void CleanupEvents()
        {
            OnDestroyed = null;
            PositionChanged = null;
            RotationChanged = null;
        }

        protected abstract void DeserializeSpecificInitialState(BinaryReader reader);
        protected abstract void DeserializeSpecificState(BinaryReader reader);
        protected abstract void HandleSpecificEvent(byte specificEventType, BinaryReader reader);
        protected abstract void InvokeSpecificStateChangedEvents();
    }

    // --- Shared Serialization Helpers ---
    internal static class SerializationUtils
    {
        public static void WriteVector3(BinaryWriter writer, Vector3 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
        public static Vector3 ReadVector3(BinaryReader reader) { return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); }
        public static void WriteQuaternion(BinaryWriter writer, Quaternion q) { writer.Write(q.X); writer.Write(q.Y); writer.Write(q.Z); writer.Write(q.W); }
        public static Quaternion ReadQuaternion(BinaryReader reader) { return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); }
        public static void WriteVector2(BinaryWriter writer, Vector2 v) { writer.Write(v.X); writer.Write(v.Y); }
        public static Vector2 ReadVector2(BinaryReader reader) { return new Vector2(reader.ReadSingle(), reader.ReadSingle()); }
    }
}