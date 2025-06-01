// File: Core/Network/Proxies/ProjectileProxy.cs
using System;
using System.IO;
using Core.Model;
using Core.Network;
using Core.Logging;
using Core.Primitives;
using Core.Client; // For ClientLevel access in ClientProxy

namespace Core.Network.Proxies
{
    public static class ProjectileProxy
    {
        internal enum ProjectileEventType : byte
        {
            HitVisual = 1, // Event to tell clients to play impact FX
        }

        // Server Proxy for Projectile (e.g., Bullet)
        public class ServerProxy : BaseServerProxy<Projectile>
        {
            public ServerProxy(Projectile entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer) { }

            // Most projectile state is set at creation and doesn't change,
            // so checksum beyond position/rotation (from base) is likely not needed
            // unless projectiles have mid-flight state changes to sync.
            // For a simple ballistic projectile, base checksum is sufficient.
            // protected override float CalculateChecksum() { ... } 

            public override void SerializeSpecificInitialState(BinaryWriter writer)
            {
                // These are the properties needed by the client for prediction and visual representation
                writer.Write(_entity.OwnerEntityId);
                writer.Write(_entity.OwnerClientId);
                SerializationUtils.WriteVector3(writer, _entity.InitialVelocity_Prediction); // Send initial velocity
                SerializationUtils.WriteDamageInfo(writer, _entity.DamagePayload); // Client might need damage type for FX
                writer.Write(_entity.MaxLifetime);
                writer.Write(_entity.ServerTimeOfSpawn_Prediction);
            }

            protected override void SerializeSpecificCorrectionState(BinaryWriter writer)
            {
                // Simple projectiles usually don't have state corrected mid-flight.
                // Position/rotation corrections are handled by BaseServerProxy if ever needed.
                // If, for example, velocity could change, it would be serialized here.
            }

            protected override void StartReplicatingInternal()
            {
                base.StartReplicatingInternal();
                _entity.OnHitServerEvent += HandleModelHit;
                // Logger.Log($"[ProjectileProxy.Server {EntityId}] Subscribed to OnHitServerEvent.");
            }

            protected override void StopReplicatingInternal()
            {
                base.StopReplicatingInternal();
                _entity.OnHitServerEvent -= HandleModelHit;
                // Logger.Log($"[ProjectileProxy.Server {EntityId}] Unsubscribed from OnHitServerEvent.");
            }

            private void HandleModelHit(Projectile projectile, DestructibleEntity victim, Vector3 hitPoint, Vector3 hitNormal, Collider hitCollider)
            {
                if (projectile.Id != _entity.Id) return;

                // Logger.Log($"[ProjectileProxy.Server {EntityId}] Model hit victim {victim.Id}. Sending HitVisual event.");
                SendEvent((byte)ProjectileEventType.HitVisual, writer =>
                {
                    writer.Write(victim.Id); // So client knows what was hit (optional for simple FX)
                    SerializationUtils.WriteVector3(writer, hitPoint);
                    SerializationUtils.WriteVector3(writer, hitNormal);
                });
            }
        }

        // Client Proxy for Projectile
        public class ClientProxy : BaseClientProxy
        {
            public int OwnerEntityId { get; private set; }
            public int OwnerClientId { get; private set; }
            public Vector3 InitialVelocity { get; private set; }
            public DamageInfo DamagePayload { get; private set; } // Store for FX, etc.
            public float MaxLifetime { get; private set; }
            public float ServerTimeOfSpawn { get; private set; }

            private float _clientSimulatedLifetime;
            private Vector3 _serverInitialSpawnPosition; // Store the absolute spawn position

            public event Action<int /*victimId*/, Vector3 /*hitPoint*/, Vector3 /*hitNormal*/> OnHitVisualsClientEvent;

            private Entity.EntityTypeEnum _concreteEntityType;
            public override Entity.EntityTypeEnum EntityType => _concreteEntityType;

            public ClientProxy(int entityId, Entity.EntityTypeEnum concreteType, ClientLevel clientLevel)
                : base(entityId, clientLevel)
            {
                _concreteEntityType = concreteType;
            }

            // BaseClientProxy.Initialize calls this after setting _simulatedPosition and _simulatedRotation
            // from the CreateEntity message.
            protected override void DeserializeSpecificInitialState(BinaryReader reader)
            {
                _serverInitialSpawnPosition = _simulatedPosition; // Capture the server-authoritative spawn position

                OwnerEntityId = reader.ReadInt32();
                OwnerClientId = reader.ReadInt32();
                InitialVelocity = SerializationUtils.ReadVector3(reader);
                DamagePayload = SerializationUtils.ReadDamageInfo(reader);
                MaxLifetime = reader.ReadSingle();
                ServerTimeOfSpawn = reader.ReadSingle();
                
                _clientSimulatedLifetime = OwningClientLevel.CurrentTime - ServerTimeOfSpawn; // Catch up lifetime
            }

            protected override void DeserializeSpecificState(BinaryReader reader)
            {
                // Simple projectiles usually don't have their state updated/corrected by server post-spawn.
            }

            protected override void InvokeSpecificStateChangedEvents()
            {
                // If any specific state properties changed that need events.
            }

            protected override void HandleSpecificEvent(byte specificEventType, BinaryReader reader)
            {
                if (Enum.IsDefined(typeof(ProjectileEventType), specificEventType))
                {
                    ProjectileEventType eventType = (ProjectileEventType)specificEventType;
                    switch (eventType)
                    {
                        case ProjectileEventType.HitVisual:
                            int victimId = reader.ReadInt32();
                            Vector3 hitPoint = SerializationUtils.ReadVector3(reader);
                            Vector3 hitNormal = SerializationUtils.ReadVector3(reader);
                            OnHitVisualsClientEvent?.Invoke(victimId, hitPoint, hitNormal);
                            // Logger.Log($"[ProjectileProxy.Client {EntityId}] Event: HitVisual. Victim: {victimId}, Point: {hitPoint}");
                            // The server will also send a DestroyEntity/VanishEntity for the projectile.
                            break;
                        default:
                            Logger.LogWarning($"[ProjectileProxy.Client {EntityId}] Received unhandled ProjectileEventType: {eventType}");
                            break;
                    }
                }
                else
                {
                    Logger.LogWarning($"[ProjectileProxy.Client {EntityId}] Received unhandled specific event type byte: {specificEventType}.");
                }
            }

            public override void Update(float deltaTime)
            {
                if (_isDestroyed) return;

                _clientSimulatedLifetime += deltaTime;

                if (_clientSimulatedLifetime >= MaxLifetime)
                {
                    // Client-side prediction that it fizzled.
                    // Server will eventually send VanishEntity if it also determined fizzle.
                    // For now, do nothing here, the server's VanishEntity will handle formal removal.
                    // If we want immediate visual disappearance:
                    // this.NotifyDestroyed(); // This might be too early if server hasn't confirmed.
                    // A better approach is for the ClientProxyPresentation to observe this lifetime and hide.
                }

                // Client-side prediction of position from the absolute start point
                float timeSinceSpawnOnClient = OwningClientLevel.CurrentTime - ServerTimeOfSpawn;
                
                Vector3 currentPredictedPosition = _serverInitialSpawnPosition + InitialVelocity * timeSinceSpawnOnClient;
                // Add gravity if implemented: currentPredictedPosition += 0.5f * gravityVector * timeSinceSpawnOnClient * timeSinceSpawnOnClient;
                
                SetSimulatedPosition(currentPredictedPosition);

                // For simple bullets, rotation typically matches initial velocity and doesn't change.
                // If it could (e.g., guided projectile), update _simulatedRotation here too.
            }

            protected override void CleanupEvents()
            {
                base.CleanupEvents();
                OnHitVisualsClientEvent = null;
            }
        }
    }
}