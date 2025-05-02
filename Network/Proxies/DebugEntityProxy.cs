// File: Scripts/Server/Core/Network/Proxies/DebugEntityProxy.cs
using System;
using System.IO;
using Server.Core.Model;
using Server.Core.Primitives;
using Core.Network;
using Core.Logging; // Use Logger

namespace Core.Network.Proxies
{
    public static class DebugEntityProxy
    {
        private enum DebugEventType : byte { SetRestPosition = 1, SpitAt = 2, ShoutAt = 3, }

        // --- Server Proxy Implementation ---
        public class ServerProxy : BaseServerProxy<DebugEntity>
        {
            // No specific state tracking needed for proactive updates anymore

            public ServerProxy(DebugEntity entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer) { }

            // --- Checksum Calculation ---
            protected override float CalculateChecksum()
            {
                // Combine hash codes of relevant state.
                // Use HashCode.Combine for better distribution than simple addition.
                // Note: float.GetHashCode can sometimes differ for visually identical values.
                // A custom checksum function might be more robust if needed.
                int hash = HashCode.Combine(
                    _entity.Position.GetHashCode(),
                    _entity.Rotation.GetHashCode(),
                    _entity.Hydration.GetHashCode(),
                    _entity.Guilt.GetHashCode()
                );
                // Return the hash code itself as the checksum (or a transformation of it)
                // It doesn't have to be human-readable, just consistent.
                return (float)hash; // Cast to float for comparison simplicity
            }


            // --- Overrides for State Serialization (Used for CreateEntity & Corrections) ---
            protected override void SerializeInitialState(BinaryWriter writer)
            {
                writer.Write(_entity.Hydration);
                writer.Write(_entity.Guilt);
            }

            protected override void SerializeState(BinaryWriter writer)
            {
                // Same as initial state for DebugEntity
                writer.Write(_entity.Hydration);
                writer.Write(_entity.Guilt);
            }

            // Removed CheckSpecificStateChanged, UpdateLastSpecificState

            // --- Overrides for Event Handling ---
            protected override void StartReplicatingInternal()
            {
                _entity.OnSetRestPositionEvent += HandleSetRestPosition;
                _entity.OnSpitAtEvent += HandleSpitAt;
                _entity.OnShoutAtEvent += HandleShoutAt;
            }

            protected override void StopReplicatingInternal()
            {
                _entity.OnSetRestPositionEvent -= HandleSetRestPosition;
                _entity.OnSpitAtEvent -= HandleSpitAt;
                _entity.OnShoutAtEvent -= HandleShoutAt;
            }

            // --- Specific Event Handlers (No changes needed) ---
            private void HandleSetRestPosition(Vector3 position) { SendEvent((byte)DebugEventType.SetRestPosition, writer => SerializationUtils.WriteVector3(writer, position)); }
            private void HandleSpitAt(Vector3 targetPosition) { SendEvent((byte)DebugEventType.SpitAt, writer => SerializationUtils.WriteVector3(writer, targetPosition)); }
            private void HandleShoutAt(Entity targetEntity) { SendEvent((byte)DebugEventType.ShoutAt, writer => writer.Write(targetEntity.Id)); }
        }


        // --- Client Proxy Implementation (No changes required here for this feature) ---
        public class ClientProxy : BaseClientProxy
        {
            private float _hydration; private float _guilt;
            public float Hydration => _hydration; public float Guilt => _guilt;
            public event Action<Vector3> OnSetRestPositionEvent;
            public event Action<Vector3> OnSpitAtEvent;
            public event Action<int> OnShoutAtEventId;
            public event Action<float> HydrationChanged;
            public event Action<float> GuiltChanged;
            public override Entity.EntityTypeEnum EntityType => Entity.EntityTypeEnum.Debug;
            public ClientProxy(int entityId) : base(entityId) { }
            protected override void DeserializeSpecificInitialState(BinaryReader reader) { _hydration = reader.ReadSingle(); _guilt = reader.ReadSingle(); }
            protected override void DeserializeSpecificState(BinaryReader reader) { _hydration = reader.ReadSingle(); _guilt = reader.ReadSingle(); }
            protected override void InvokeSpecificStateChangedEvents() { HydrationChanged?.Invoke(_hydration); GuiltChanged?.Invoke(_guilt); }
            protected override void HandleSpecificEvent(byte specificEventType, BinaryReader reader)
            {
                DebugEventType eventType = (DebugEventType)specificEventType;
                switch (eventType)
                {
                    case DebugEventType.SetRestPosition: OnSetRestPositionEvent?.Invoke(SerializationUtils.ReadVector3(reader)); break;
                    case DebugEventType.SpitAt: OnSpitAtEvent?.Invoke(SerializationUtils.ReadVector3(reader)); break;
                    case DebugEventType.ShoutAt: OnShoutAtEventId?.Invoke(reader.ReadInt32()); break;
                    default: Logger.LogWarning($"[DebugEntity.ClientProxy {EntityId}] Received unknown specific event type: {specificEventType}"); break;
                }
            }
            protected override void CleanupEvents()
            {
                base.CleanupEvents();
                OnSetRestPositionEvent = null; OnSpitAtEvent = null; OnShoutAtEventId = null;
                HydrationChanged = null; GuiltChanged = null;
            }
        }
    }
}