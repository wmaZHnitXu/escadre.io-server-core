// File: Core/Network/Proxies/BaseServerProxy.cs
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
        public Vector3 Position => _entity.Position;
        public Quaternion Rotation => _entity.Rotation;

        protected BaseServerProxy(TEntity entity, IServerNetworkLayer networkLayer)
        {
            _entity = entity ?? throw new ArgumentNullException(nameof(entity));
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
        }

        public virtual void StartReplicating()
        {
            _entity.OnDeathEvent += HandleEntityFinalDeath;
            _entity.OnDestructionEvent += HandleEntityLoudDestruction;
            StartReplicatingInternal();
        }

        public virtual void StopReplicating()
        {
            _entity.OnDeathEvent -= HandleEntityFinalDeath;
            _entity.OnDestructionEvent -= HandleEntityLoudDestruction;
            StopReplicatingInternal();
        }

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
                return needsCorrection;
            }
            catch (EndOfStreamException eof) { Logger.LogError($"[BaseServerProxy {EntityId}] Error reading client checksum (EOS): {eof.Message}"); return true; }
            catch (Exception ex) { Logger.LogError($"[BaseServerProxy {EntityId}] Error checking ClientSyncState: {ex.Message}"); return true; }
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
            if (_entity.IsDead) return;
            _networkLayer.BroadcastRelevant(EntityId, MessageType.EntityEvent, writer =>
            {
                writer.Write(specificEventType);
                serializeEventPayloadAction?.Invoke(writer);
            });
        }

        private void HandleEntityLoudDestruction(Entity destroyedEntity)
        {
            _networkLayer.BroadcastRelevant(EntityId, MessageType.DestroyEntity, writer => { /* No payload */ });
        }

        private void HandleEntityFinalDeath(Entity deadEntity) { }

        protected virtual float CalculateChecksum()
        {
            return HashCode.Combine(_entity.Position.GetHashCode(), _entity.Rotation.GetHashCode());
        }

        protected abstract void SerializeSpecificCorrectionState(BinaryWriter writer);
        protected virtual void StartReplicatingInternal() { }
        protected virtual void StopReplicatingInternal() { }
    }

    public abstract class BaseClientProxy : IClientProxy
    {
        public int EntityId { get; }
        public abstract Entity.EntityTypeEnum EntityType { get; }

        protected Vector3 _simulatedPosition;
        protected Quaternion _simulatedRotation;
        public Vector3 Position => _simulatedPosition;
        public Quaternion Rotation => _simulatedRotation;

        protected bool _isDestroyed = false;

        public event Action OnDestroyed;
        public event Action OnLoudDestructionSignaled;
        public event Action<Vector3> PositionChanged;
        public event Action<Quaternion> RotationChanged;

        protected BaseClientProxy(int entityId) { EntityId = entityId; }

        public virtual void Initialize(BinaryReader reader)
        {
            // Initial state from server is the authoritative start point
            _simulatedPosition = SerializationUtils.ReadVector3(reader);
            _simulatedRotation = SerializationUtils.ReadQuaternion(reader);
            DeserializeSpecificInitialState(reader);

            // Force invoke events on initial set so presentation snaps immediately
            PositionChanged?.Invoke(_simulatedPosition);
            RotationChanged?.Invoke(_simulatedRotation);
        }

        public void HandleNetworkMessage(MessageType messageType, BinaryReader reader)
        {
            if (_isDestroyed && messageType != MessageType.VanishEntity) { return; }
            try
            {
                switch (messageType)
                {
                    case MessageType.DestroyEntity:
                        OnLoudDestructionSignaled?.Invoke();
                        break;
                    case MessageType.VanishEntity:
                        NotifyDestroyed();
                        break;
                    case MessageType.UpdateState:
                        DeserializeAndUpdateState(reader);
                        break;
                    case MessageType.EntityEvent:
                        DeserializeAndDispatchEntityEvent(reader);
                        break;
                    default:
                        Logger.LogWarning($"[ClientProxy {EntityId}] Received unhandled message type by proxy: {messageType}");
                        break;
                }
            }
            catch (Exception ex) { Logger.LogError($"[ClientProxy {EntityId}] Error processing message {messageType}: {ex.Message}"); }
        }

        protected virtual void DeserializeAndUpdateState(BinaryReader reader) // Authoritative State Correction
        {
            var serverAuthPosition = SerializationUtils.ReadVector3(reader);
            var serverAuthRotation = SerializationUtils.ReadQuaternion(reader);

            // Hard snap to server's authoritative state
            SetSimulatedPositionAndRotation(serverAuthPosition, serverAuthRotation);

            DeserializeSpecificState(reader);
            InvokeSpecificStateChangedEvents();
        }

        protected virtual void DeserializeAndDispatchEntityEvent(BinaryReader reader)
        {
            byte specificEventType = reader.ReadByte();
            HandleSpecificEvent(specificEventType, reader);
        }

        public virtual void Update(float clientSimulatedServerTime, float deltaTime)
        {
            // Base implementation does nothing; derived proxies implement their simulation.
            // If derived classes update _simulatedPosition or _simulatedRotation,
            // they are responsible for calling SetSimulatedPosition/Rotation to trigger events.
        }

        public void NotifyDestroyed() {
            if (_isDestroyed) return; _isDestroyed = true;
            OnDestroyed?.Invoke(); CleanupEvents();
        }

        protected void SetSimulatedPosition(Vector3 newPosition)
        {
            if (_simulatedPosition != newPosition)
            {
                _simulatedPosition = newPosition;
                PositionChanged?.Invoke(_simulatedPosition);
            }
        }

        protected void SetSimulatedRotation(Quaternion newRotation)
        {
            if (_simulatedRotation != newRotation)
            {
                _simulatedRotation = newRotation;
                RotationChanged?.Invoke(_simulatedRotation);
            }
        }

        protected void SetSimulatedPositionAndRotation(Vector3 newPosition, Quaternion newRotation)
        {
            bool posChanged = _simulatedPosition != newPosition;
            bool rotChanged = _simulatedRotation != newRotation;

            _simulatedPosition = newPosition;
            _simulatedRotation = newRotation;

            if (posChanged) PositionChanged?.Invoke(_simulatedPosition);
            if (rotChanged) RotationChanged?.Invoke(_simulatedRotation);
        }

        protected virtual void CleanupEvents() {
            OnDestroyed = null; OnLoudDestructionSignaled = null; PositionChanged = null; RotationChanged = null;
        }
        protected abstract void DeserializeSpecificInitialState(BinaryReader reader);
        protected abstract void DeserializeSpecificState(BinaryReader reader);
        protected abstract void HandleSpecificEvent(byte specificEventType, BinaryReader reader);
        protected abstract void InvokeSpecificStateChangedEvents();
    }
}