// File: Core/Network/Proxies/DebugEntityProxy.cs
using System;
using System.IO;
using Core.Model;
using Core.Primitives;
using Core.Network;
using Core.Logging;
using Core.Client; // For ClientLevel

namespace Core.Network.Proxies
{
    public static class DebugEntityProxy
    {
        private enum DebugEventType : byte
        {
            SetRestPosition = 1,
            SpitAt = 2,
            ShoutAt = 3,
        }

        public class ServerProxy : BaseServerProxy<DebugEntity>
        {
            public ServerProxy(DebugEntity entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer) { }

            protected override float CalculateChecksum()
            {
                int hash = HashCode.Combine(
                    _entity.Position.GetHashCode(),
                    _entity.Rotation.GetHashCode(),
                    _entity.Hydration.GetHashCode(),
                    _entity.Guilt.GetHashCode()
                );
                return (float)hash;
            }

            public override void SerializeSpecificInitialState(BinaryWriter writer)
            {
                writer.Write(_entity.Hydration);
                writer.Write(_entity.Guilt);
            }

            protected override void SerializeSpecificCorrectionState(BinaryWriter writer)
            {
                writer.Write(_entity.Hydration);
                writer.Write(_entity.Guilt);
            }

            protected override void StartReplicatingInternal()
            {
                _entity.OnSetRestPositionEvent += HandleSetRestPosition;
                _entity.OnSpitAtEvent += HandleSpitAt;
                _entity.OnShoutAtEvent += HandleShoutAt;
                 Logger.Log($"[DebugEntityProxy.Server {EntityId}] Subscribed to DebugEntity events.");
            }

            protected override void StopReplicatingInternal()
            {
                _entity.OnSetRestPositionEvent -= HandleSetRestPosition;
                _entity.OnSpitAtEvent -= HandleSpitAt;
                _entity.OnShoutAtEvent -= HandleShoutAt;
                 Logger.Log($"[DebugEntityProxy.Server {EntityId}] Unsubscribed from DebugEntity events.");
            }

            private void HandleSetRestPosition(Vector3 position) { SendEvent((byte)DebugEventType.SetRestPosition, writer => SerializationUtils.WriteVector3(writer, position)); }
            private void HandleSpitAt(Vector3 targetPosition) { SendEvent((byte)DebugEventType.SpitAt, writer => SerializationUtils.WriteVector3(writer, targetPosition)); }
            private void HandleShoutAt(Entity targetEntity) { SendEvent((byte)DebugEventType.ShoutAt, writer => writer.Write(targetEntity.Id)); }
        }

        public class ClientProxy : BaseClientProxy
        {
            private float _hydration;
            private float _guilt;

            public float Hydration => _hydration;
            public float Guilt => _guilt;

            public event Action<Vector3> OnSetRestPositionEvent;
            public event Action<Vector3> OnSpitAtEvent;
            public event Action<int> OnShoutAtEventId; 

            public event Action<float> HydrationChanged;
            public event Action<float> GuiltChanged;

            public override Entity.EntityTypeEnum EntityType => Entity.EntityTypeEnum.Debug;

            // Constructor updated to take ClientLevel
            public ClientProxy(int entityId, ClientLevel clientLevel) 
                : base(entityId, clientLevel) // Pass clientLevel to base
            { }

            protected override void DeserializeSpecificInitialState(BinaryReader reader)
            {
                _hydration = reader.ReadSingle();
                _guilt = reader.ReadSingle();
            }

            protected override void DeserializeSpecificState(BinaryReader reader)
            {
                var oldHydration = _hydration;
                var oldGuilt = _guilt;

                _hydration = reader.ReadSingle();
                _guilt = reader.ReadSingle();

                if (Math.Abs(_hydration - oldHydration) > float.Epsilon) HydrationChanged?.Invoke(_hydration);
                if (Math.Abs(_guilt - oldGuilt) > float.Epsilon) GuiltChanged?.Invoke(_guilt);
            }

            protected override void InvokeSpecificStateChangedEvents()
            {
            }

            protected override void HandleSpecificEvent(byte specificEventType, BinaryReader reader)
            {
                if (Enum.IsDefined(typeof(DebugEventType), specificEventType))
                {
                    DebugEventType eventType = (DebugEventType)specificEventType;
                    switch (eventType)
                    {
                        case DebugEventType.SetRestPosition:
                            Vector3 pos = SerializationUtils.ReadVector3(reader);
                            OnSetRestPositionEvent?.Invoke(pos);
                            Logger.Log($"[DebugEntityProxy.Client {EntityId}] Event: SetRestPosition to {pos}");
                            break;
                        case DebugEventType.SpitAt:
                            Vector3 targetPos = SerializationUtils.ReadVector3(reader);
                            OnSpitAtEvent?.Invoke(targetPos);
                            Logger.Log($"[DebugEntityProxy.Client {EntityId}] Event: SpitAt {targetPos}");
                            break;
                        case DebugEventType.ShoutAt:
                            int targetId = reader.ReadInt32();
                            OnShoutAtEventId?.Invoke(targetId);
                            Logger.Log($"[DebugEntityProxy.Client {EntityId}] Event: ShoutAt TargetId {targetId}");
                            break;
                        default:
                            Logger.LogWarning($"[DebugEntityProxy.Client {EntityId}] Received unknown DebugEventType: {eventType}");
                            break;
                    }
                }
                else
                {
                    Logger.LogWarning($"[DebugEntityProxy.Client {EntityId}] Received unhandled specific event type byte: {specificEventType}. This proxy does not call base.HandleSpecificEvent.");
                }
            }

            protected override void CleanupEvents()
            {
                base.CleanupEvents();
                OnSetRestPositionEvent = null;
                OnSpitAtEvent = null;
                OnShoutAtEventId = null;
                HydrationChanged = null;
                GuiltChanged = null;
            }
        }
    }
}