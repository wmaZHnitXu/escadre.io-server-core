// File: Scripts/Server/Core/Network/Proxies/BaseServerProxy.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public Vector3 Position => _entity.Position; // Common property
        public Quaternion Rotation => _entity.Rotation; // Common property

        protected BaseServerProxy(TEntity entity, IServerNetworkLayer networkLayer)
        {
            _entity = entity ?? throw new ArgumentNullException(nameof(entity));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            // Moved OnDeathEvent subscription to StartReplicatingInternal or specific proxies
            // to avoid subscribing if the proxy is created but never replicated.
        }

        public virtual void StartReplicating()
        {
            _entity.OnDeathEvent += HandleEntityDeath; // Subscribe on actual replication start
            Logger.Log($"[BaseServerProxy {EntityId}, Type {_entity.EntityType}] Started replicating.");
            StartReplicatingInternal();
        }

        public virtual void StopReplicating()
        {
            _entity.OnDeathEvent -= HandleEntityDeath; // Unsubscribe on replication stop
            Logger.Log($"[BaseServerProxy {EntityId}, Type {_entity.EntityType}] Stopping replicating.");
            StopReplicatingInternal();
        }

        public void SendDestroyMessage(IEnumerable<int> targetClientIds)
        {
            if (targetClientIds == null || !targetClientIds.Any()) return;
            _networkLayer.SendDestroyCommand(EntityId, targetClientIds);
        }

        public bool CheckClientSyncState(BinaryReader reader)
        {
            if (_entity.IsDead) return false; // No need to sync dead entities
            try
            {
                float clientChecksum = reader.ReadSingle();
                float serverChecksum = CalculateChecksum(); // Now virtual
                float difference = Math.Abs(serverChecksum - clientChecksum);
                bool needsCorrection = difference > ChecksumThreshold;
                if (needsCorrection)
                {
                    Logger.Log($"[BaseServerProxy {EntityId}] Checksum diff ({difference}) > threshold ({ChecksumThreshold}). Server: {serverChecksum}, Client: {clientChecksum}. Correction needed.");
                }
                return needsCorrection;
            }
            catch (EndOfStreamException eof) { Logger.LogError($"[BaseServerProxy {EntityId}] Error reading client checksum (End of Stream): {eof.Message}"); return true; /* Assume correction needed */ }
            catch (Exception ex) { Logger.LogError($"[BaseServerProxy {EntityId}] Error checking ClientSyncState: {ex.Message}\nStackTrace: {ex.StackTrace}"); return true; /* Assume correction needed */ }
        }

        public abstract void SerializeSpecificInitialState(BinaryWriter writer);

        public void SerializeCorrectionState(BinaryWriter writer)
        {
            // Common state always serialized for correction
            SerializationUtils.WriteVector3(writer, _entity.Position);
            SerializationUtils.WriteQuaternion(writer, _entity.Rotation);
            SerializeSpecificCorrectionState(writer); // Now abstract or virtual
        }

        protected void SendEvent(byte specificEventType, Action<BinaryWriter> serializeEventPayloadAction)
        {
            if (_entity.IsDead) return; // Don't send events for dead entities
            _networkLayer.BroadcastRelevant(EntityId, MessageType.EntityEvent, writer =>
            {
                writer.Write(specificEventType);
                serializeEventPayloadAction?.Invoke(writer);
            });
        }

        private void HandleEntityDeath(Entity deadEntity)
        {
            // StopReplicating(); // This is called by ServerReplicationManager for the proxy that died.
            // The ServerReplicationManager will also ensure SendDestroyMessage is called.
            // This handler primarily ensures that if the proxy is still active for some reason when entity dies,
            // it stops its internal event subscriptions.
            // However, ServerReplicationManager.HandleEntityDeath should call proxy.StopReplicating(),
            // which then calls StopReplicatingInternal().
            // So this HandleEntityDeath here is mostly a safeguard or if StopReplicating isn't called externally.
            // For clarity, let StopReplicating (called externally) handle the _entity.OnDeathEvent -= HandleEntityDeath.
            Logger.Log($"[BaseServerProxy {EntityId}] Notified of entity death. Internal cleanup in StopReplicatingInternal will occur.");
        }

        // --- Abstract/Virtual methods for derived classes ---
        /// <summary> Calculates the checksum for the entity's replicated state. </summary>
        protected virtual float CalculateChecksum()
        {
            // Base checksum includes common properties like Position and Rotation
            // Derived proxies should call base.CalculateChecksum() and combine with their specific state.
            return HashCode.Combine(
                _entity.Position.GetHashCode(),
                _entity.Rotation.GetHashCode()
            );
        }

        /// <summary> Serializes entity-specific state for correction messages. </summary>
        protected abstract void SerializeSpecificCorrectionState(BinaryWriter writer);

        /// <summary> Called when replication starts; subscribe to entity-specific events here. </summary>
        protected virtual void StartReplicatingInternal() { }

        /// <summary> Called when replication stops; unsubscribe from entity-specific events here. </summary>
        protected virtual void StopReplicatingInternal() { }
    }

    // --- Base Client Proxy (remains largely the same but good to review context) ---
    public abstract class BaseClientProxy : IClientProxy
    {
        public int EntityId { get; }
        public abstract Entity.EntityTypeEnum EntityType { get; } // Provided by concrete proxy
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
            if (_isDestroyed && messageType != MessageType.DestroyEntity) // Allow DestroyEntity even if already marked (rare)
            {
                 Logger.LogWarning($"[ClientProxy {EntityId}] Ignoring message {messageType} for already destroyed proxy.");
                 return;
            }
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
                    // CreateEntity is handled by the factory creating the proxy and calling Initialize.
                    // DestroyEntity is handled by ClientEntityManager calling NotifyDestroyed.
                    default:
                        Logger.LogWarning($"[ClientProxy {EntityId}] Received unhandled message type by proxy: {messageType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[ClientProxy {EntityId}] Error processing message {messageType}: {ex.Message} \nStackTrace: {ex.StackTrace}");
                // Potentially mark as desynced or request resync
            }
        }

        public void NotifyDestroyed()
        {
            if (_isDestroyed) return;
            _isDestroyed = true;
            Logger.Log($"[ClientProxy {EntityId}] Notified Destroyed.");
            OnDestroyed?.Invoke();
            CleanupEvents();
        }

        public virtual void Initialize(BinaryReader reader) // Called by ClientProxyFactory
        {
            _position = SerializationUtils.ReadVector3(reader);
            _rotation = SerializationUtils.ReadQuaternion(reader);
            DeserializeSpecificInitialState(reader); // Abstract
            Logger.Log($"[ClientProxy {EntityId}, Type {EntityType}] Initialized. Pos: {_position}, Rot: {_rotation}");
        }

        protected virtual void DeserializeAndUpdateState(BinaryReader reader)
        {
            var oldPos = _position;
            var oldRot = _rotation;
            _position = SerializationUtils.ReadVector3(reader);
            _rotation = SerializationUtils.ReadQuaternion(reader);

            DeserializeSpecificState(reader); // Abstract

            if (_position != oldPos) PositionChanged?.Invoke(_position);
            if (_rotation != oldRot) RotationChanged?.Invoke(_rotation);
            InvokeSpecificStateChangedEvents(); // Abstract
            // Logger.Log($"[ClientProxy {EntityId}] State Updated. Pos: {_position}");
        }

        protected virtual void DeserializeAndDispatchEvent(BinaryReader reader)
        {
            byte specificEventType = reader.ReadByte();
            HandleSpecificEvent(specificEventType, reader); // Abstract
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
        protected abstract void InvokeSpecificStateChangedEvents(); // For aggregated change events after state deserialization
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

        public static void WriteDamageInfo(BinaryWriter writer, DamageInfo info)
        {
            writer.Write(info.Amount);
            writer.Write((byte)info.Type);
            WriteVector3(writer, info.HitPoint);
            WriteVector3(writer, info.Direction);
            writer.Write(info.AttackerId.HasValue);
            if(info.AttackerId.HasValue) writer.Write(info.AttackerId.Value);
            writer.Write(info.AttackerOwnerClientId.HasValue);
            if(info.AttackerOwnerClientId.HasValue) writer.Write(info.AttackerOwnerClientId.Value);
        }
        public static DamageInfo ReadDamageInfo(BinaryReader reader)
        {
            float amount = reader.ReadSingle();
            DamageType type = (DamageType)reader.ReadByte();
            Vector3 hitPoint = ReadVector3(reader);
            Vector3 direction = ReadVector3(reader);
            int? attackerId = null;
            if (reader.ReadBoolean()) attackerId = reader.ReadInt32();
            int? attackerOwnerClientId = null;
            if (reader.ReadBoolean()) attackerOwnerClientId = reader.ReadInt32();
            return new DamageInfo(amount, type, hitPoint, direction, attackerId, attackerOwnerClientId);
        }
    }
}