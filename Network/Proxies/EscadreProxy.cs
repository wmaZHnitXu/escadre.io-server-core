// File: Core/Network/Proxies/EscadreProxy.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core.Model;
using Core.Primitives;
using Core.Network;
using Core.Logging;
using Core.Client; // For ClientLevel (though client proxy uses base.OwningClientLevel)

namespace Core.Network.Proxies
{
    // This enum is for specific events on the Escadre entity itself.
    // Common state like Nickname, Resources, Formation will be part of CreateEntity/UpdateState.
    // However, frequent updates like Resources and Formation might benefit from specific events
    // to avoid sending the whole state block if only one thing changed.
    // For now, let's try to include them in the standard state updates.
    // If checksums and UpdateState become too heavy, we can introduce these events.
    internal enum EscadreEventType : byte
    {
        // Example: ResourcesChanged = 1, (payload: new resource amount)
        // Example: FormationLayoutChanged = 2, (payload: new formation slot list)
        // Example: NicknameChanged = 3 (payload: new nickname string)
        ShopInfoUpdated = 4 // Special event to send shop designs if not part of initial sync or if they change
    }

    // ClientEscadreState is no longer a separate class. Its fields will be properties on EscadreProxy.ClientProxy
    // public class ClientEscadreState { ... } // REMOVED

    public static class EscadreProxy
    {
        public class ServerProxy : BaseServerProxy<Escadre> // Inherit from BaseServerProxy
        {
            // private readonly Level _level; // From Escadre model, for shop access. Escadre itself has _level.

            public ServerProxy(Escadre entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer)
            {
                // _level = entity._level; // Access level via the entity
            }

            protected override float CalculateChecksum()
            {
                int baseHash = base.CalculateChecksum().GetHashCode(); // Pos, Rot from BaseServerProxy
                int formationHash = 0;
                foreach(var slot in _entity.CurrentFormation.Slots)
                {
                    formationHash = HashCode.Combine(formationHash, slot.ShipEntityId, slot.RelativeOffset);
                }
                // Shop designs don't typically change per-escadre or frequently, so not ideal for checksum.
                // They are better as initial state or a separate global message/event.
                return HashCode.Combine(baseHash, _entity.Nickname, _entity.Resources, formationHash);
            }

            public override void SerializeSpecificInitialState(BinaryWriter writer)
            {
                // BaseServerProxy handles common Entity state like Position, Rotation via its CreateEntity flow.
                // We serialize Escadre-specific state here.
                writer.Write(_entity.OwnerClientId); // Important for client to identify its own escadre
                writer.Write(_entity.Nickname ?? string.Empty);
                writer.Write(_entity.Resources);

                writer.Write(_entity.CurrentFormation.Slots.Count);
                foreach (var slot in _entity.CurrentFormation.Slots)
                {
                    writer.Write(slot.ShipEntityId.HasValue ? slot.ShipEntityId.Value : -1);
                    SerializationUtils.WriteVector2(writer, slot.RelativeOffset);
                }

                // Include Shop Designs in initial state for now
                var designs = _entity.Level.GameShop.AvailableShipDesigns;
                writer.Write(designs.Count);
                foreach (var design in designs)
                {
                    writer.Write(design.DesignId);
                    writer.Write(design.Name);
                    writer.Write(design.Cost);
                    writer.Write((byte)design.ShipEntityType);
                }
            }

            protected override void SerializeSpecificCorrectionState(BinaryWriter writer)
            {
                // BaseServerProxy handles common Entity state like Position, Rotation.
                // We serialize Escadre-specific state here.
                writer.Write(_entity.OwnerClientId); // Should not change, but send for consistency
                writer.Write(_entity.Nickname ?? string.Empty);
                writer.Write(_entity.Resources);

                writer.Write(_entity.CurrentFormation.Slots.Count);
                foreach (var slot in _entity.CurrentFormation.Slots)
                {
                    writer.Write(slot.ShipEntityId.HasValue ? slot.ShipEntityId.Value : -1);
                    SerializationUtils.WriteVector2(writer, slot.RelativeOffset);
                }
                // Shop designs are not typically part of correction state unless they changed globally
                // and we decide to push them this way. For now, assume they are initial-only or via specific event.
            }
            
            protected override void StartReplicatingInternal()
            {
                base.StartReplicatingInternal(); // Handles base Entity events if any
                // Subscribe to Escadre-specific model events to trigger state updates/events
                _entity.OnResourcesChanged += HandleModelResourcesChanged;
                _entity.OnFormationChanged += HandleModelFormationChanged;
                // If Nickname could change, subscribe to an OnNicknameChanged event.
                Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Subscribed to Escadre model events.");
            }

            protected override void StopReplicatingInternal()
            {
                base.StopReplicatingInternal();
                _entity.OnResourcesChanged -= HandleModelResourcesChanged;
                _entity.OnFormationChanged -= HandleModelFormationChanged;
                Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Unsubscribed from Escadre model events.");
            }

            private void HandleModelResourcesChanged(int newAmount)
            {
                // When resources change, the checksum will likely change.
                // The standard _ClientSyncState mechanism will trigger an UpdateState if client's view is stale.
                // No specific event needed if checksum approach is reliable for this.
                // For immediate push, we could send an event, but let's rely on sync for now.
                 Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Resources changed. Checksum will reflect this.");
            }

            private void HandleModelFormationChanged(Formation formation)
            {
                // Similar to resources, checksum will change.
                 Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Formation changed. Checksum will reflect this.");
            }
            
            // Example: If shop designs change globally, could send a specific event
            public void SendShopDesignsUpdate() // Call this if GameShop.AvailableShipDesigns changes
            {
                 var designs = _entity.Level.GameShop.AvailableShipDesigns;
                 Logger.Log($"[EscadreProxy.Server {EntityId}] Sending ShopInfoUpdated event. Count: {designs.Count}");
                 SendEvent((byte)EscadreEventType.ShopInfoUpdated, writer =>
                 {
                    writer.Write(designs.Count);
                    foreach (var design in designs)
                    {
                        writer.Write(design.DesignId);
                        writer.Write(design.Name);
                        writer.Write(design.Cost);
                        writer.Write((byte)design.ShipEntityType);
                    }
                 });
            }
        }
        
        public class ClientProxy : BaseClientProxy // Inherit from BaseClientProxy
        {
            public override Entity.EntityTypeEnum EntityType => Entity.EntityTypeEnum.Escadre;

            // Properties that were in ClientEscadreState
            public int OwnerClientId { get; private set; }
            public string Nickname { get; private set; }
            public int Resources { get; private set; }
            public List<FormationSlot> FormationSlots { get; } = new List<FormationSlot>();
            public List<ShipDesign> AvailableShopDesigns { get; } = new List<ShipDesign>();

            // Events for UI to subscribe to
            public event Action OnNicknameChanged;
            public event Action OnResourcesChanged;
            public event Action OnFormationChanged;
            public event Action OnShopDesignsChanged;


            public ClientProxy(int entityId, ClientLevel clientLevel)
                : base(entityId, clientLevel) { }

            protected override void DeserializeSpecificInitialState(BinaryReader reader)
            {
                // BaseClientProxy handles Position, Rotation
                OwnerClientId = reader.ReadInt32();
                Nickname = reader.ReadString();
                Resources = reader.ReadInt32();

                int formationCount = reader.ReadInt32();
                FormationSlots.Clear();
                for (int i = 0; i < formationCount; i++)
                {
                    int shipId = reader.ReadInt32();
                    Vector2 offset = SerializationUtils.ReadVector2(reader);
                    FormationSlots.Add(new FormationSlot(offset, shipId == -1 ? (int?)null : shipId ));
                }

                int designCount = reader.ReadInt32();
                AvailableShopDesigns.Clear();
                for (int i = 0; i < designCount; i++)
                {
                    int designId = reader.ReadInt32();
                    string name = reader.ReadString();
                    int cost = reader.ReadInt32();
                    Entity.EntityTypeEnum shipEntityType = (Entity.EntityTypeEnum)reader.ReadByte();
                    AvailableShopDesigns.Add(new ShipDesign(designId, name, cost, shipEntityType));
                }
                
                Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Initialized. Owner:{OwnerClientId}, Nick:{Nickname}, Res:{Resources}, Slots:{FormationSlots.Count}, ShopDesigns:{AvailableShopDesigns.Count}");
                InvokeAllChangedEvents(); // Invoke events after initial state is set
            }

            protected override void DeserializeSpecificState(BinaryReader reader)
            {
                // BaseClientProxy handles Position, Rotation
                var oldOwner = OwnerClientId; // Should not change, but read for consistency
                var oldNickname = Nickname;
                var oldResources = Resources;
                // For formation, compare collections if complex, or just signal change.
                // For simplicity, we'll signal change if counts differ or just always signal.

                OwnerClientId = reader.ReadInt32();
                Nickname = reader.ReadString();
                Resources = reader.ReadInt32();

                int formationCount = reader.ReadInt32();
                FormationSlots.Clear(); // Rebuild formation list
                for (int i = 0; i < formationCount; i++)
                {
                    int shipId = reader.ReadInt32();
                    Vector2 offset = SerializationUtils.ReadVector2(reader);
                    FormationSlots.Add(new FormationSlot(offset, shipId == -1 ? (int?)null : shipId ));
                }
                // Shop designs usually not in correction unless explicitly sent.
                // If they were here, deserialize and fire OnShopDesignsChanged.

                Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] State Updated. Owner:{OwnerClientId}, Nick:{Nickname}, Res:{Resources}, Slots:{FormationSlots.Count}");

                if(OwnerClientId != oldOwner) Logger.LogWarning($"[EscadreProxy.Client EntityId:{EntityId}] OwnerClientId changed from {oldOwner} to {OwnerClientId}, this is unusual.");
                if(Nickname != oldNickname) OnNicknameChanged?.Invoke();
                if(Resources != oldResources) OnResourcesChanged?.Invoke();
                OnFormationChanged?.Invoke(); // Always signal formation change on UpdateState for simplicity
            }

            protected override void HandleSpecificEvent(byte specificEventType, BinaryReader reader)
            {
                if (Enum.IsDefined(typeof(EscadreEventType), specificEventType))
                {
                    EscadreEventType eventType = (EscadreEventType)specificEventType;
                    switch (eventType)
                    {
                        case EscadreEventType.ShopInfoUpdated:
                            int designCount = reader.ReadInt32();
                            AvailableShopDesigns.Clear();
                            for (int i = 0; i < designCount; i++)
                            {
                                int designId = reader.ReadInt32();
                                string name = reader.ReadString();
                                int cost = reader.ReadInt32();
                                Entity.EntityTypeEnum shipEntityType = (Entity.EntityTypeEnum)reader.ReadByte();
                                AvailableShopDesigns.Add(new ShipDesign(designId, name, cost, shipEntityType));
                            }
                            Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Processed ShopInfoUpdated event. Count: {AvailableShopDesigns.Count}");
                            OnShopDesignsChanged?.Invoke();
                            break;
                        // Handle other EscadreEventType cases if defined (e.g., for fine-grained resource/formation updates)
                        default:
                            Logger.LogWarning($"[EscadreProxy.Client EntityId:{EntityId}] Unhandled EscadreEventType: {eventType}");
                            break;
                    }
                }
                else
                {
                     Logger.LogWarning($"[EscadreProxy.Client EntityId:{EntityId}] Received unhandled specific event type byte: {specificEventType}. Proxy does not call base for this.");
                }
            }
            
            protected override void InvokeSpecificStateChangedEvents()
            {
                // This is called after DeserializeSpecificState.
                // Events are already invoked within DeserializeSpecificState based on changes.
                // This method can be left empty or used if there's a general "StateChanged" event.
            }

            private void InvokeAllChangedEvents()
            {
                OnNicknameChanged?.Invoke();
                OnResourcesChanged?.Invoke();
                OnFormationChanged?.Invoke();
                OnShopDesignsChanged?.Invoke();
            }
            
            protected override void CleanupEvents()
            {
                base.CleanupEvents(); // Cleans up PositionChanged, RotationChanged etc.
                OnNicknameChanged = null;
                OnResourcesChanged = null;
                OnFormationChanged = null;
                OnShopDesignsChanged = null;
            }
        }
    }
}