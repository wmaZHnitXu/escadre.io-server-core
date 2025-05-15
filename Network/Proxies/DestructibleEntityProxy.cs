// File: Core/Network/Proxies/DestructibleEntityProxy.cs
using System;
using System.IO;
using Core.Model;
using Core.Network;
using Core.Logging;

namespace Core.Network.Proxies
{
    public static class DestructibleEntityProxy
    {
        protected enum DestructibleEventType : byte
        {
            TookDamageVisual = 1, // For visual/audio effects, includes DamageInfo
            // HealthChanged event is implicitly handled by state sync (CurrentHealth)
        }

        // --- Server Proxy Implementation ---
        // Generic constraint to allow ShipProxy to inherit with Ship type
        public class ServerProxy<TDestructible> : BaseServerProxy<TDestructible>
            where TDestructible : DestructibleEntity
        {
            public ServerProxy(TDestructible entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer) { }

            protected override float CalculateChecksum()
            {
                // Combine base checksum (Position, Rotation) with DestructibleEntity state
                int baseHash = base.CalculateChecksum().GetHashCode(); // Get hash from float for combining
                return HashCode.Combine(
                    baseHash,
                    _entity.CurrentHealth.GetHashCode(),
                    _entity.MaxHealth.GetHashCode() // MaxHealth might change due to upgrades
                );
            }

            public override void SerializeSpecificInitialState(BinaryWriter writer)
            {
                writer.Write(_entity.CurrentHealth);
                writer.Write(_entity.MaxHealth);
            }

            protected override void SerializeSpecificCorrectionState(BinaryWriter writer)
            {
                writer.Write(_entity.CurrentHealth);
                writer.Write(_entity.MaxHealth);
            }

            protected override void StartReplicatingInternal()
            {
                base.StartReplicatingInternal();
                _entity.OnDamaged += HandleEntityDamaged;
                Logger.Log($"[DestructibleEntityProxy.Server {EntityId}] Subscribed to OnDamaged.");
            }

            protected override void StopReplicatingInternal()
            {
                base.StopReplicatingInternal();
                _entity.OnDamaged -= HandleEntityDamaged;
                 Logger.Log($"[DestructibleEntityProxy.Server {EntityId}] Unsubscribed from OnDamaged.");
            }

            private void HandleEntityDamaged(DamageInfo damageInfo, DestructibleEntity victim)
            {
                if (victim.Id != _entity.Id) return; // Should not happen if subscribed correctly

                // Send an event for visual/audio cues on the client
                SendEvent((byte)DestructibleEventType.TookDamageVisual, writer =>
                {
                    SerializationUtils.WriteDamageInfo(writer, damageInfo);
                });
                 Logger.Log($"[DestructibleEntityProxy.Server {EntityId}] Sent TookDamageVisual event. Damage: {damageInfo.Amount}");
            }
        }

        // --- Client Proxy Implementation ---
        public class ClientProxy : BaseClientProxy
        {
            public float CurrentHealth { get; protected set; }
            public float MaxHealth { get; protected set; }

            public event Action<float, float> HealthChanged; // current, max
            public event Action<DamageInfo> TookDamageVisuals; // For effects

            // EntityType will be set by the concrete proxy that might inherit from this (e.g. ShipProxy)
            // Or, if this proxy is used directly, the factory needs to set it.
            // For now, assume a derived proxy or factory sets EntityType.
            private Entity.EntityTypeEnum _concreteEntityType;
            public override Entity.EntityTypeEnum EntityType => _concreteEntityType;

            public ClientProxy(int entityId, Entity.EntityTypeEnum concreteType) : base(entityId)
            {
                _concreteEntityType = concreteType;
            }
             // Constructor for direct use if not inherited, though less likely
            public ClientProxy(int entityId) : base(entityId)
            {
                 Logger.LogWarning($"[DestructibleEntityProxy.Client {EntityId}] Created without concrete type. EntityType will be default.");
            }


            protected override void DeserializeSpecificInitialState(BinaryReader reader)
            {
                CurrentHealth = reader.ReadSingle();
                MaxHealth = reader.ReadSingle();
                HealthChanged?.Invoke(CurrentHealth, MaxHealth); // Invoke on initial set
            }

            protected override void DeserializeSpecificState(BinaryReader reader)
            {
                var oldHealth = CurrentHealth;
                var oldMaxHealth = MaxHealth;

                CurrentHealth = reader.ReadSingle();
                MaxHealth = reader.ReadSingle();

                if (Math.Abs(CurrentHealth - oldHealth) > float.Epsilon || Math.Abs(MaxHealth - oldMaxHealth) > float.Epsilon)
                {
                    HealthChanged?.Invoke(CurrentHealth, MaxHealth);
                }
            }

            protected override void InvokeSpecificStateChangedEvents()
            {
                // HealthChanged is invoked directly in DeserializeSpecificState
            }

            protected override void HandleSpecificEvent(byte specificEventType, BinaryReader reader)
            {
                DestructibleEventType eventType = (DestructibleEventType)specificEventType;
                switch (eventType)
                {
                    case DestructibleEventType.TookDamageVisual:
                        DamageInfo dInfo = SerializationUtils.ReadDamageInfo(reader);
                        TookDamageVisuals?.Invoke(dInfo);
                        Logger.Log($"[DestructibleEntityProxy.Client {EntityId}] Event: TookDamageVisual. Damage: {dInfo.Amount}");
                        break;
                    default:
                        Logger.LogWarning($"[DestructibleEntityProxy.Client {EntityId}] Received unknown specific event type: {specificEventType}");
                        break;
                }
            }

            protected override void CleanupEvents()
            {
                base.CleanupEvents();
                HealthChanged = null;
                TookDamageVisuals = null;
            }
        }
    }
}