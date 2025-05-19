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
    // Made public to resolve CS0051
    public enum BaseProxyEventType : byte 
    {
        Teleported = 100 
    }

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
            _entity.OnTeleported += HandleEntityTeleported; 
            StartReplicatingInternal();
        }

        public virtual void StopReplicating()
        {
            _entity.OnDeathEvent -= HandleEntityFinalDeath;
            _entity.OnDestructionEvent -= HandleEntityLoudDestruction;
            _entity.OnTeleported -= HandleEntityTeleported; 
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

        // This method sends events specific to derived proxies.
        // For base proxy events like Teleported, a new method or direct call is used.
        protected void SendEvent(byte specificEventType, Action<BinaryWriter> serializeEventPayloadAction)
        {
            if (_entity.IsDead) return;
            _networkLayer.BroadcastRelevant(EntityId, MessageType.EntityEvent, writer =>
            {
                writer.Write(specificEventType); // This is the specific event type from the derived proxy
                serializeEventPayloadAction?.Invoke(writer);
            });
        }
        
        // Method to send base proxy events
        protected void SendBaseProxyEvent(BaseProxyEventType baseEventType, Action<BinaryWriter> serializeEventPayloadAction)
        {
            if (_entity.IsDead) return;
            _networkLayer.BroadcastRelevant(EntityId, MessageType.EntityEvent, writer =>
            {
                writer.Write((byte)baseEventType); // Cast the base event type to byte
                serializeEventPayloadAction?.Invoke(writer);
            });
        }


        private void HandleEntityLoudDestruction(Entity destroyedEntity)
        {
            if (destroyedEntity.Id != EntityId) return;
            _networkLayer.BroadcastRelevant(EntityId, MessageType.DestroyEntity, writer => { /* No payload */ });
        }

        private void HandleEntityFinalDeath(Entity deadEntity) 
        {
            if (deadEntity.Id != EntityId) return;
        }

        private void HandleEntityTeleported(Entity teleportedEntity, Vector3 newPosition, Quaternion newRotation)
        {
            if (teleportedEntity.Id != EntityId) return;
            Logger.Log($"[BaseServerProxy {EntityId}] Entity teleported. Sending event.");
            // Use SendBaseProxyEvent for base events
            SendBaseProxyEvent(BaseProxyEventType.Teleported, writer =>
            {
                SerializationUtils.WriteVector3(writer, newPosition);
                SerializationUtils.WriteQuaternion(writer, newRotation);
            });
        }

        protected virtual float CalculateChecksum()
        {
            return HashCode.Combine(_entity.Position.GetHashCode(), _entity.Rotation.GetHashCode());
        }

        protected abstract void SerializeSpecificCorrectionState(BinaryWriter writer);
        protected virtual void StartReplicatingInternal() { }
        protected virtual void StopReplicatingInternal() { }
    }
}