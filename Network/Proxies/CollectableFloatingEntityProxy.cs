// File: Core/Network/Proxies/CollectableFloatingEntityProxy.cs
using System;
using System.IO;
using Core.Model;
using Core.Network;
using Core.Logging;
using Core.Primitives;
using Core.Client; // For ClientLevel access in ClientProxy

namespace Core.Network.Proxies
{
    public static class CollectableFloatingEntityProxy
    {
        // Events specific to CollectableFloatingEntity that need to be replicated
        internal enum CollectableEventType : byte
        {
            CollectedVisual = 1, // Event to tell clients to play collection FX
        }

        // Server Proxy for ResourceBox (or any CollectableFloatingEntity)
        public class ServerProxy<TCollectable> : BaseServerProxy<TCollectable>
            where TCollectable : CollectableFloatingEntity
        {
            public ServerProxy(TCollectable entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer) { }

            protected override float CalculateChecksum()
            {
                int baseHash = base.CalculateChecksum().GetHashCode(); // Position, Rotation
                // Add specific properties if they can change and need sync
                // FinalCollectionDistance and SuckSpeed are typically fixed after creation.
                // ResourceAmount for a ResourceBox is also usually fixed.
                int specificHash = 0;
                if (_entity is ResourceBox resourceBox)
                {
                    specificHash = resourceBox.ResourceAmount.GetHashCode();
                }
                
                return HashCode.Combine(baseHash, 
                                        _entity.FinalCollectionDistance.GetHashCode(), 
                                        _entity.SuckSpeed.GetHashCode(), 
                                        specificHash);
            }

            public override void SerializeSpecificInitialState(BinaryWriter writer)
            {
                // Serialize common collectable properties first
                writer.Write(_entity.FinalCollectionDistance);
                writer.Write(_entity.SuckSpeed);

                // Then type-specific properties
                if (_entity is ResourceBox resourceBox)
                {
                    writer.Write(resourceBox.ResourceAmount);
                }
                // else if (_entity is OtherCollectableType otherType) { ... }
            }

            protected override void SerializeSpecificCorrectionState(BinaryWriter writer)
            {
                // These properties usually don't change post-creation, but send for consistency if needed
                writer.Write(_entity.FinalCollectionDistance);
                writer.Write(_entity.SuckSpeed);

                if (_entity is ResourceBox resourceBox)
                {
                    writer.Write(resourceBox.ResourceAmount);
                }
            }

            protected override void StartReplicatingInternal()
            {
                base.StartReplicatingInternal(); 
                _entity.OnCollectedServerEvent += HandleModelCollected;
                // Logger.Log($"[CollectableFloatingEntityProxy.Server {EntityId}] Subscribed to OnCollectedServerEvent.");
            }

            protected override void StopReplicatingInternal()
            {
                base.StopReplicatingInternal();
                _entity.OnCollectedServerEvent -= HandleModelCollected;
                // Logger.Log($"[CollectableFloatingEntityProxy.Server {EntityId}] Unsubscribed from OnCollectedServerEvent.");
            }

            private void HandleModelCollected(CollectableFloatingEntity collectedEntity, Ship collectingShip)
            {
                if (collectedEntity.Id != _entity.Id) return;

                // Logger.Log($"[CollectableFloatingEntityProxy.Server {EntityId}] Model collected by Ship {collectingShip.Id}. Sending CollectedVisual event.");
                SendEvent((byte)CollectableEventType.CollectedVisual, writer =>
                {
                    writer.Write(collectingShip.Id); 
                });
            }
        }

        // Client Proxy for ResourceBox (or any CollectableFloatingEntity)
        public class ClientProxy : BaseClientProxy
        {
            public float FinalCollectionDistance { get; private set; }
            public float SuckSpeed { get; private set; }

            // Specific properties for derived types, e.g., ResourceBox
            public int ResourceAmount { get; private set; } // Only for ResourceBox type

            public event Action<int /*collectingShipId*/> OnCollectedVisuals;
            // Event if client needs to know about the speeds/distances changing (unlikely for these props)
            // public event Action OnCollectableParamsChanged; 

            private Entity.EntityTypeEnum _concreteEntityType;
            public override Entity.EntityTypeEnum EntityType => _concreteEntityType;

            public ClientProxy(int entityId, Entity.EntityTypeEnum concreteType, ClientLevel clientLevel)
                : base(entityId, clientLevel)
            {
                _concreteEntityType = concreteType;
            }

            protected override void DeserializeSpecificInitialState(BinaryReader reader)
            {
                FinalCollectionDistance = reader.ReadSingle();
                SuckSpeed = reader.ReadSingle();

                if (_concreteEntityType == Entity.EntityTypeEnum.ResourceBox)
                {
                    ResourceAmount = reader.ReadInt32();
                }
                InvokeSpecificStateChangedEvents(); 
            }

            protected override void DeserializeSpecificState(BinaryReader reader)
            {
                // These properties usually don't change, but if they could, handle it here.
                // For now, they are part of correction state for completeness.
                var oldFinalCollectionDistance = FinalCollectionDistance;
                var oldSuckSpeed = SuckSpeed;
                FinalCollectionDistance = reader.ReadSingle();
                SuckSpeed = reader.ReadSingle();

                bool changed = Math.Abs(FinalCollectionDistance - oldFinalCollectionDistance) > float.Epsilon ||
                               Math.Abs(SuckSpeed - oldSuckSpeed) > float.Epsilon;

                if (_concreteEntityType == Entity.EntityTypeEnum.ResourceBox)
                {
                    var oldResourceAmount = ResourceAmount;
                    ResourceAmount = reader.ReadInt32();
                    if (ResourceAmount != oldResourceAmount) changed = true;
                }
                
                if (changed) InvokeSpecificStateChangedEvents();
            }
            
            protected override void InvokeSpecificStateChangedEvents()
            {
                // Example: OnCollectableParamsChanged?.Invoke();
                // If ResourceAmount had its own Changed event:
                // if (_concreteEntityType == Entity.EntityTypeEnum.ResourceBox) { ResourceAmountChanged?.Invoke(ResourceAmount); }
            }

            protected override void HandleSpecificEvent(byte specificEventType, BinaryReader reader)
            {
                if (Enum.IsDefined(typeof(CollectableEventType), specificEventType))
                {
                    CollectableEventType eventType = (CollectableEventType)specificEventType;
                    switch (eventType)
                    {
                        case CollectableEventType.CollectedVisual:
                            int collectingShipId = reader.ReadInt32();
                            OnCollectedVisuals?.Invoke(collectingShipId);
                            Logger.Log($"[CollectableFloatingEntityProxy.Client {EntityId}] Event: CollectedVisual by Ship {collectingShipId}.");
                            break;
                        default:
                            Logger.LogWarning($"[CollectableFloatingEntityProxy.Client {EntityId}] Received unhandled CollectableEventType: {eventType}");
                            break;
                    }
                }
                else
                {
                    Logger.LogWarning($"[CollectableFloatingEntityProxy.Client {EntityId}] Received unhandled specific event type byte: {specificEventType}. This proxy does not handle it.");
                }
            }

            protected override void CleanupEvents()
            {
                base.CleanupEvents();
                OnCollectedVisuals = null;
                // OnCollectableParamsChanged = null;
            }
        }
    }
}