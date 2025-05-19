// File: Core/Network/Proxies/DestructibleEntityProxy.cs
using System;
using System.IO;
using Core.Model;
using Core.Network;
using Core.Logging;
using Core.Client; // For ClientLevel

namespace Core.Network.Proxies
{
    public static class DestructibleEntityProxy
    {
        protected enum DestructibleEventType : byte
        {
            TookDamageVisual = 1, 
        }

        public class ServerProxy<TDestructible> : BaseServerProxy<TDestructible>
            where TDestructible : DestructibleEntity
        {
            public ServerProxy(TDestructible entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer) { }

            protected override float CalculateChecksum()
            {
                int baseHash = base.CalculateChecksum().GetHashCode(); 
                return HashCode.Combine(
                    baseHash,
                    _entity.CurrentHealth.GetHashCode(),
                    _entity.MaxHealth.GetHashCode() 
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
                if (victim.Id != _entity.Id) return; 

                SendEvent((byte)DestructibleEventType.TookDamageVisual, writer =>
                {
                    SerializationUtils.WriteDamageInfo(writer, damageInfo);
                });
                 Logger.Log($"[DestructibleEntityProxy.Server {EntityId}] Sent TookDamageVisual event. Damage: {damageInfo.Amount}");
            }
        }

        public class ClientProxy : BaseClientProxy
        {
            public float CurrentHealth { get; protected set; }
            public float MaxHealth { get; protected set; }

            public event Action<float, float> HealthChanged; 
            public event Action<DamageInfo> TookDamageVisuals; 

            private Entity.EntityTypeEnum _concreteEntityType;
            public override Entity.EntityTypeEnum EntityType => _concreteEntityType;

            // Constructor updated to take ClientLevel
            public ClientProxy(int entityId, Entity.EntityTypeEnum concreteType, ClientLevel clientLevel) 
                : base(entityId, clientLevel) // Pass clientLevel to base
            {
                _concreteEntityType = concreteType;
            }
            
            // This constructor might be problematic if not all paths provide ClientLevel.
            // It's better to ensure ClientLevel is always passed.
            // For now, keeping it but it should ideally be removed or handled carefully.
            public ClientProxy(int entityId, ClientLevel clientLevel) 
                : base(entityId, clientLevel) // Pass clientLevel to base
            {
                 Logger.LogWarning($"[DestructibleEntityProxy.Client {EntityId}] Created without concrete type. EntityType will be default.");
                 // _concreteEntityType would be default(Entity.EntityTypeEnum) which is Debug. This might be an issue.
            }


            protected override void DeserializeSpecificInitialState(BinaryReader reader)
            {
                CurrentHealth = reader.ReadSingle();
                MaxHealth = reader.ReadSingle();
                HealthChanged?.Invoke(CurrentHealth, MaxHealth); 
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
            }

            protected override void HandleSpecificEvent(byte specificEventType, BinaryReader reader)
            {
                if (Enum.IsDefined(typeof(DestructibleEventType), specificEventType))
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
                            Logger.LogWarning($"[DestructibleEntityProxy.Client {EntityId}] Received unknown DestructibleEventType: {eventType}");
                            break;
                    }
                }
                else
                {
                     Logger.LogWarning($"[DestructibleEntityProxy.Client {EntityId}] Received unhandled specific event type byte: {specificEventType}. Could be for a derived proxy.");
                     // It's important that derived proxies (like ShipProxy) call base.HandleSpecificEvent if they don't handle the event themselves.
                     // However, BaseClientProxy.HandleSpecificEvent is abstract, so this path shouldn't be hit if specificEventType is not a DestructibleEventType.
                     // This implies an issue if a specificEventType is received here that is not a DestructibleEventType.
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