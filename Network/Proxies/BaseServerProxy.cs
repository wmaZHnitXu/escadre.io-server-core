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
    // BaseProxyEventType is no longer needed for LoudDestruction signal,
    // as MessageType.DestroyEntity serves this purpose.
    // internal enum BaseProxyEventType : byte { LoudDestruction = 250 }

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
        }

        public virtual void StartReplicating()
        {
            _entity.OnDeathEvent += HandleEntityFinalDeath; // For SRM to know the entity is truly gone model-side
            _entity.OnDestructionEvent += HandleEntityLoudDestruction; // For broadcasting "loud kill" signal
            Logger.Log($"[BaseServerProxy {EntityId}, Type {_entity.EntityType}] Started replicating. Subscribed to OnDeath & OnDestruction.");
            StartReplicatingInternal();
        }

        public virtual void StopReplicating()
        {
            _entity.OnDeathEvent -= HandleEntityFinalDeath;
            _entity.OnDestructionEvent -= HandleEntityLoudDestruction;
            Logger.Log($"[BaseServerProxy {EntityId}, Type {_entity.EntityType}] Stopping replicating. Unsubscribed from OnDeath & OnDestruction.");
            StopReplicatingInternal();
        }

        // Renamed from SendDestroyMessage to SendVanishMessage
        public void SendVanishMessage(IEnumerable<int> targetClientIds)
        {
            if (targetClientIds == null || !targetClientIds.Any()) return;
            _networkLayer.SendVanishCommand(EntityId, targetClientIds);
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
                    Logger.Log($"[BaseServerProxy {EntityId}] Checksum diff ({difference}) > threshold ({ChecksumThreshold}). Server: {serverChecksum}, Client: {clientChecksum}. Correction needed.");
                }
                return needsCorrection;
            }
            catch (EndOfStreamException eof) { Logger.LogError($"[BaseServerProxy {EntityId}] Error reading client checksum (End of Stream): {eof.Message}"); return true; }
            catch (Exception ex) { Logger.LogError($"[BaseServerProxy {EntityId}] Error checking ClientSyncState: {ex.Message}\nStackTrace: {ex.StackTrace}"); return true; }
        }

        public abstract void SerializeSpecificInitialState(BinaryWriter writer);

        public void SerializeCorrectionState(BinaryWriter writer)
        {
            SerializationUtils.WriteVector3(writer, _entity.Position);
            SerializationUtils.WriteQuaternion(writer, _entity.Rotation);
            SerializeSpecificCorrectionState(writer);
        }

        // This method is for entity-specific gameplay events, not lifecycle events like destruction.
        protected void SendEvent(byte specificEventType, Action<BinaryWriter> serializeEventPayloadAction)
        {
            if (_entity.IsDead) return;
            _networkLayer.BroadcastRelevant(EntityId, MessageType.EntityEvent, writer =>
            {
                writer.Write(specificEventType);
                serializeEventPayloadAction?.Invoke(writer);
            });
        }

        private void HandleEntityLoudDestruction(Entity destroyedEntity)
        {
            // This event is only fired for non-silent kills.
            // Broadcast MessageType.DestroyEntity to signal "loud destruction, play effects".
            Logger.Log($"[BaseServerProxy {EntityId}] Entity undergoing loud destruction (OnDestructionEvent). Broadcasting DestroyEntity signal.");
            _networkLayer.BroadcastRelevant(EntityId, MessageType.DestroyEntity, writer => { /* No payload needed */ });
        }

        private void HandleEntityFinalDeath(Entity deadEntity)
        {
            // This event signifies the entity is truly gone from the model's perspective.
            // ServerReplicationManager will typically call StopReplicating() on this proxy
            // and then proxy.SendVanishMessage().
            Logger.Log($"[BaseServerProxy {EntityId}] Notified of entity final death (OnDeathEvent).");
        }


        protected virtual float CalculateChecksum()
        {
            return HashCode.Combine( _entity.Position.GetHashCode(), _entity.Rotation.GetHashCode() );
        }
        protected abstract void SerializeSpecificCorrectionState(BinaryWriter writer);
        protected virtual void StartReplicatingInternal() { }
        protected virtual void StopReplicatingInternal() { }
    }


    public abstract class BaseClientProxy : IClientProxy
    {
        public int EntityId { get; }
        public abstract Entity.EntityTypeEnum EntityType { get; }
        protected Vector3 _position;
        protected Quaternion _rotation;
        protected bool _isDestroyed = false; // True when VanishEntity is processed
        public Vector3 Position => _position;
        public Quaternion Rotation => _rotation;

        public event Action OnDestroyed; // For final cleanup, invoked by NotifyDestroyed (on VanishEntity)
        public event Action OnLoudDestructionSignaled; // For effects, invoked on DestroyEntity
        public event Action<Vector3> PositionChanged;
        public event Action<Quaternion> RotationChanged;

        protected BaseClientProxy(int entityId) { EntityId = entityId; }

        public void HandleNetworkMessage(MessageType messageType, BinaryReader reader)
        {
            // If truly destroyed (vanished), only process VanishEntity again (idempotent) or log error.
            // If merely "loud destruction signaled", other messages like UpdateState might still come if server sends them before Vanish.
            if (_isDestroyed && messageType != MessageType.VanishEntity)
            {
                // Logger.LogWarning($"[ClientProxy {EntityId}] Ignoring message {messageType} for fully destroyed (vanished) proxy.");
                return;
            }

            try
            {
                switch (messageType)
                {
                    case MessageType.DestroyEntity: // Loud destruction signal
                        Logger.Log($"[ClientProxy {EntityId}, Type {EntityType}] Received DestroyEntity (Loud Destruction signal).");
                        OnLoudDestructionSignaled?.Invoke();
                        // DO NOT call NotifyDestroyed() here.
                        break;
                    case MessageType.VanishEntity: // Actual removal signal
                        Logger.Log($"[ClientProxy {EntityId}, Type {EntityType}] Received VanishEntity signal.");
                        NotifyDestroyed(); // This will set _isDestroyed = true
                        break;
                    case MessageType.UpdateState:
                        DeserializeAndUpdateState(reader);
                        break;
                    case MessageType.EntityEvent:
                        DeserializeAndDispatchEvent(reader);
                        break;
                    default:
                        Logger.LogWarning($"[ClientProxy {EntityId}] Received unhandled message type by proxy: {messageType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[ClientProxy {EntityId}] Error processing message {messageType}: {ex.Message} \nStackTrace: {ex.StackTrace}");
            }
        }

        public void NotifyDestroyed() // Should only be called upon receiving VanishEntity
        {
            if (_isDestroyed) return;
            _isDestroyed = true;
            Logger.Log($"[ClientProxy {EntityId}, Type {EntityType}] Processing Vanish: Proxy fully destroyed.");
            OnDestroyed?.Invoke();
            CleanupEvents();
        }

        public virtual void Initialize(BinaryReader reader) { /* ... as before ... */
            _position = SerializationUtils.ReadVector3(reader);
            _rotation = SerializationUtils.ReadQuaternion(reader);
            DeserializeSpecificInitialState(reader);
            Logger.Log($"[ClientProxy {EntityId}, Type {EntityType}] Initialized. Pos: {_position}, Rot: {_rotation}");
        }
        protected virtual void DeserializeAndUpdateState(BinaryReader reader) { /* ... as before ... */
            var oldPos = _position; var oldRot = _rotation;
            _position = SerializationUtils.ReadVector3(reader); _rotation = SerializationUtils.ReadQuaternion(reader);
            DeserializeSpecificState(reader);
            if (_position != oldPos) PositionChanged?.Invoke(_position);
            if (_rotation != oldRot) RotationChanged?.Invoke(_rotation);
            InvokeSpecificStateChangedEvents();
        }

        protected virtual void DeserializeAndDispatchEvent(BinaryReader reader)
        {
            byte specificEventType = reader.ReadByte();
            // BaseProxyEventType for LoudDestruction is now handled by MessageType.DestroyEntity
            // So, this method is purely for derived proxy specific events.
            HandleSpecificEvent(specificEventType, reader);
        }

        protected virtual void CleanupEvents()
        {
            OnDestroyed = null;
            OnLoudDestructionSignaled = null; // Clear this new event
            PositionChanged = null;
            RotationChanged = null;
        }

        protected abstract void DeserializeSpecificInitialState(BinaryReader reader);
        protected abstract void DeserializeSpecificState(BinaryReader reader);
        protected abstract void HandleSpecificEvent(byte specificEventType, BinaryReader reader);
        protected abstract void InvokeSpecificStateChangedEvents();
    }

    // SerializationUtils remains the same
    internal static class SerializationUtils { /* ... as before ... */
        public static void WriteVector3(BinaryWriter writer, Vector3 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
        public static Vector3 ReadVector3(BinaryReader reader) { return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); }
        public static void WriteQuaternion(BinaryWriter writer, Quaternion q) { writer.Write(q.X); writer.Write(q.Y); writer.Write(q.Z); writer.Write(q.W); }
        public static Quaternion ReadQuaternion(BinaryReader reader) { return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); }
        public static void WriteVector2(BinaryWriter writer, Vector2 v) { writer.Write(v.X); writer.Write(v.Y); }
        public static Vector2 ReadVector2(BinaryReader reader) { return new Vector2(reader.ReadSingle(), reader.ReadSingle()); }
        public static void WriteDamageInfo(BinaryWriter writer, DamageInfo info) { /* ... */
            writer.Write(info.Amount); writer.Write((byte)info.Type); WriteVector3(writer, info.HitPoint); WriteVector3(writer, info.Direction);
            writer.Write(info.AttackerId.HasValue); if(info.AttackerId.HasValue) writer.Write(info.AttackerId.Value);
            writer.Write(info.AttackerOwnerClientId.HasValue); if(info.AttackerOwnerClientId.HasValue) writer.Write(info.AttackerOwnerClientId.Value);
        }
        public static DamageInfo ReadDamageInfo(BinaryReader reader) { /* ... */
            float amount = reader.ReadSingle(); DamageType type = (DamageType)reader.ReadByte(); Vector3 hitPoint = ReadVector3(reader); Vector3 direction = ReadVector3(reader);
            int? attackerId = null; if (reader.ReadBoolean()) attackerId = reader.ReadInt32();
            int? attackerOwnerClientId = null; if (reader.ReadBoolean()) attackerOwnerClientId = reader.ReadInt32();
            return new DamageInfo(amount, type, hitPoint, direction, attackerId, attackerOwnerClientId);
        }
    }
}