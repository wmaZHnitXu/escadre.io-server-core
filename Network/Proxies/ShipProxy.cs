// File: Core/Network/Proxies/ShipProxy.cs
using System;
using System.IO;
using Core.Model;
using Core.Network; // For IServerNetworkLayer
using Core.Logging;
using Core.Primitives; // For Vector types if needed for specific state

namespace Core.Network.Proxies
{
    public static class ShipProxy
    {
        // No ship-specific events defined yet beyond what DestructibleEntityProxy handles
        // protected enum ShipEventType : byte {}

        // --- Server Proxy Implementation ---
        // Inherits from DestructibleEntityProxy.ServerProxy, typed with Ship
        public class ServerProxy : DestructibleEntityProxy.ServerProxy<Ship>
        {
            public ServerProxy(Ship entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer) { }

            protected override float CalculateChecksum()
            {
                // Combine DestructibleEntity checksum (Pos, Rot, Health) with Ship-specific state
                int baseHash = base.CalculateChecksum().GetHashCode();
                return HashCode.Combine(
                    baseHash,
                    _entity.CurrentSpeed.GetHashCode(),
                    _entity.MaxSpeed.GetHashCode(), // If these stats can change (e.g. upgrades) and need checksumming
                    _entity.AttackRange.GetHashCode()
                    // Add other relevant stats if they are dynamic and checksummed
                );
            }

            public override void SerializeSpecificInitialState(BinaryWriter writer)
            {
                base.SerializeSpecificInitialState(writer); // Writes CurrentHealth, MaxHealth

                // Ship-specific initial state
                writer.Write(_entity.OwningEscadreClientId);
                writer.Write(_entity.CurrentSpeed); // Initial speed might be 0

                // Write ship stats (these are abstract, so concrete ship provides them)
                writer.Write(_entity.MaxSpeed);
                writer.Write(_entity.TurnRate);
                writer.Write(_entity.AttackDamage);
                writer.Write(_entity.AttackRange);
                writer.Write(_entity.AttackCooldown);
            }

            protected override void SerializeSpecificCorrectionState(BinaryWriter writer)
            {
                base.SerializeSpecificCorrectionState(writer); // Writes CurrentHealth, MaxHealth

                // Ship-specific correction state
                writer.Write(_entity.CurrentSpeed);

                // If stats like MaxSpeed, AttackDamage can change frequently outside of major "Upgrade" events,
                // they should be synced here. Otherwise, an "Upgraded" event might trigger a full resync
                // or send the new stat block. For simplicity, syncing them here if they might change.
                writer.Write(_entity.MaxSpeed);
                writer.Write(_entity.TurnRate);
                writer.Write(_entity.AttackDamage);
                writer.Write(_entity.AttackRange);
                writer.Write(_entity.AttackCooldown);
            }

            protected override void StartReplicatingInternal()
            {
                base.StartReplicatingInternal(); // Subscribes to OnDamaged from DestructibleEntityProxy
                // Subscribe to any Ship-specific events here
                // e.g., _entity.OnWeaponFired += HandleWeaponFired;
                Logger.Log($"[ShipProxy.Server {EntityId}] Subscribed to specific Ship events (if any).");
            }

            protected override void StopReplicatingInternal()
            {
                base.StopReplicatingInternal();
                // Unsubscribe from Ship-specific events
                Logger.Log($"[ShipProxy.Server {EntityId}] Unsubscribed from specific Ship events (if any).");
            }
        }

        // --- Client Proxy Implementation ---
        // Inherits from DestructibleEntityProxy.ClientProxy
        public class ClientProxy : DestructibleEntityProxy.ClientProxy
        {
            public int OwningEscadreClientId { get; private set; }
            public float CurrentSpeed { get; private set; }

            // Stats (mirrored from server)
            public float MaxSpeed { get; private set; }
            public float TurnRate { get; private set; }
            public float AttackDamage { get; private set; }
            public float AttackRange { get; private set; }
            public float AttackCooldown { get; private set; }

            public event Action<float> CurrentSpeedChanged;
            public event Action StatsChanged; // Generic event if multiple stats change (e.g., after an upgrade)

            // EntityType is handled by the base DestructibleEntityProxy.ClientProxy,
            // which gets the concrete type from the factory.

            public ClientProxy(int entityId, Entity.EntityTypeEnum concreteType)
                : base(entityId, concreteType) { }


            protected override void DeserializeSpecificInitialState(BinaryReader reader)
            {
                base.DeserializeSpecificInitialState(reader); // Reads CurrentHealth, MaxHealth

                OwningEscadreClientId = reader.ReadInt32();
                CurrentSpeed = reader.ReadSingle();

                MaxSpeed = reader.ReadSingle();
                TurnRate = reader.ReadSingle();
                AttackDamage = reader.ReadSingle();
                AttackRange = reader.ReadSingle();
                AttackCooldown = reader.ReadSingle();

                // Initial invocation of change events
                CurrentSpeedChanged?.Invoke(CurrentSpeed);
                StatsChanged?.Invoke();
            }

            protected override void DeserializeSpecificState(BinaryReader reader)
            {
                base.DeserializeSpecificState(reader); // Reads CurrentHealth, MaxHealth

                var oldSpeed = CurrentSpeed;
                CurrentSpeed = reader.ReadSingle();

                var oldMaxSpeed = MaxSpeed;
                MaxSpeed = reader.ReadSingle();
                TurnRate = reader.ReadSingle(); // Assuming TurnRate is also synced if it can change
                AttackDamage = reader.ReadSingle();
                AttackRange = reader.ReadSingle();
                AttackCooldown = reader.ReadSingle();


                if (Math.Abs(CurrentSpeed - oldSpeed) > float.Epsilon)
                {
                    CurrentSpeedChanged?.Invoke(CurrentSpeed);
                }

                // Check if any stat changed significantly to raise a general StatsChanged event
                if (Math.Abs(MaxSpeed - oldMaxSpeed) > float.Epsilon || /* other stat comparisons */ true) // Simplification for now
                {
                     StatsChanged?.Invoke();
                }
            }

            protected override void HandleSpecificEvent(byte specificEventType, BinaryReader reader)
            {
                // If Ship had its own event types, handle them here.
                // For now, it relies on DestructibleEntityProxy for TookDamageVisual.
                // Cast specificEventType to ShipEventType if defined.
                // switch ((ShipEventType)specificEventType) { ... }
                base.HandleSpecificEvent(specificEventType, reader); // Pass to base if not a ship-specific event
                                                                     // Or, if no ship-specific events, this method can be removed
                                                                     // and base class handles all events.
                                                                     // For now, explicitly call base to ensure Destructible events are processed.
            }

            protected override void InvokeSpecificStateChangedEvents()
            {
                base.InvokeSpecificStateChangedEvents(); // Handles HealthChanged from DestructibleEntityProxy
                // CurrentSpeedChanged and StatsChanged are invoked directly in DeserializeSpecificState
            }

            protected override void CleanupEvents()
            {
                base.CleanupEvents();
                CurrentSpeedChanged = null;
                StatsChanged = null;
            }
        }
    }
}