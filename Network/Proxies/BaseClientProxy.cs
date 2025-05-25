// File: Core/Network/Proxies/BaseClientProxy.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core.Model;
using Core.Primitives;
using Core.Network;
using Core.Logging;
using Core.Client; 
using Core.Ocean; 

namespace Core.Network.Proxies
{
    public abstract class BaseClientProxy : IClientProxy
    {
        public int EntityId { get; }
        public abstract Entity.EntityTypeEnum EntityType { get; }

        protected Vector3 _simulatedPosition;
        protected Quaternion _simulatedRotation;
        public Vector3 Position => _simulatedPosition;
        public Quaternion Rotation => _simulatedRotation;

        protected bool _isDestroyed = false;

        public ClientLevel OwningClientLevel { get; } 

        protected IFloatingBehavior _clientFloatingBehavior; 

        public event Action OnDestroyed;
        public event Action OnLoudDestructionSignaled;
        public event Action<Vector3> PositionChanged;
        public event Action<Quaternion> RotationChanged;

        protected BaseClientProxy(int entityId, ClientLevel clientLevel) 
        { 
            EntityId = entityId;
            OwningClientLevel = clientLevel ?? throw new ArgumentNullException(nameof(clientLevel));
        }

        public virtual void Initialize(BinaryReader reader)
        {
            _simulatedPosition = SerializationUtils.ReadVector3(reader);
            _simulatedRotation = SerializationUtils.ReadQuaternion(reader);
            DeserializeSpecificInitialState(reader); // This is where derived proxies (like ShipProxy) can set _clientFloatingBehavior

            // Initial server position already includes ocean effects.
            // Client-side floating simulation in Update() will maintain this.
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

        protected virtual void DeserializeAndUpdateState(BinaryReader reader) 
        {
            var serverAuthPosition = SerializationUtils.ReadVector3(reader);
            var serverAuthRotation = SerializationUtils.ReadQuaternion(reader);

            SetSimulatedPositionAndRotation(serverAuthPosition, serverAuthRotation);

            DeserializeSpecificState(reader);
            InvokeSpecificStateChangedEvents();
        }

        protected virtual void DeserializeAndDispatchEntityEvent(BinaryReader reader)
        {
            byte eventTypeByte = reader.ReadByte();
            if (Enum.IsDefined(typeof(BaseProxyEventType), eventTypeByte))
            {
                HandleBaseProxyEvent((BaseProxyEventType)eventTypeByte, reader);
            }
            else
            {
                HandleSpecificEvent(eventTypeByte, reader);
            }
        }
        
        protected virtual void HandleBaseProxyEvent(BaseProxyEventType eventType, BinaryReader reader)
        {
            switch (eventType)
            {
                case BaseProxyEventType.Teleported:
                    Vector3 newPos = SerializationUtils.ReadVector3(reader);
                    Quaternion newRot = SerializationUtils.ReadQuaternion(reader);
                    SetSimulatedPositionAndRotation(newPos, newRot); 
                    Logger.Log($"[BaseClientProxy {EntityId}] Handled Teleported event. New Pos: {newPos}, New Rot: {newRot}");
                    break;
                default:
                    Logger.LogWarning($"[BaseClientProxy {EntityId}] Received unhandled BaseProxyEventType: {eventType}");
                    break;
            }
        }


        public virtual void Update(float deltaTime)
        {
            // Derived proxies like ShipProxy will override this to combine their
            // planar movement simulation with ocean floating effects.
            // This base implementation is a fallback if a proxy is floatable but doesn't have
            // its own complex movement.
            if (!_isDestroyed && _clientFloatingBehavior != null && OwningClientLevel.IsOceanInitialized)
            {
                float sampleX = _simulatedPosition.X;
                float sampleZ = _simulatedPosition.Z;

                Vector3 oceanDisplacement = OwningClientLevel.OceanDataProvider.GetDisplacement(sampleX, sampleZ, OwningClientLevel.CurrentTime);
                Vector3 oceanNormal = OwningClientLevel.OceanDataProvider.GetNormal(sampleX, sampleZ, OwningClientLevel.CurrentTime);
                Vector3 targetSurfacePoint = new Vector3(
                    sampleX + oceanDisplacement.X,
                    oceanDisplacement.Y,
                    sampleZ + oceanDisplacement.Z
                );
                
                var tempEntity = new TempEntityForFloatingLogic(_simulatedPosition, _simulatedRotation);
                _clientFloatingBehavior.ApplyFloating(tempEntity, targetSurfacePoint, oceanNormal, deltaTime);
                
                if (tempEntity.Position != _simulatedPosition || tempEntity.Rotation != _simulatedRotation)
                {
                    SetSimulatedPositionAndRotation(tempEntity.Position, tempEntity.Rotation);
                }
            }
        }

        // Internal helper for client-side floating logic if Entity class cannot be instantiated directly.
        // This is a minimal stand-in for what IFloatingBehavior.ApplyFloating expects.
        protected internal class TempEntityForFloatingLogic : Entity 
        {
            public TempEntityForFloatingLogic(Vector3 pos, Quaternion rot) : base(null) 
            { 
                // Directly set private fields to bypass AddEntity in base constructor
                // This is a bit of a hack due to Entity's constructor adding to Level.
                // A cleaner way would be an Entity constructor that doesn't auto-add, or ApplyFloating taking raw values.
                typeof(Entity).GetField("_position", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(this, pos);
                typeof(Entity).GetField("_rotation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(this, rot);
            }
            public override EntityTypeEnum EntityType => (EntityTypeEnum)(-1); // Dummy
            public override void Update(float delta) { /* NOP */ }
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

            if (Math.Abs(newPosition.X) < 0.01f && 
                Math.Abs(newPosition.Z) < 0.01f &&
                _simulatedPosition != Vector3.Zero && // Check against default only if _simulatedPosition is initialized
                Math.Abs(newPosition.Y - _simulatedPosition.X) < 0.01f &&
                (_simulatedPosition.X != 0f || Math.Abs(newPosition.Y) > 0.01f) && 
                newPosition != _simulatedPosition) 
            {
                Logger.LogWarning($"[BaseClientProxy {EntityId}] SetSimulatedPositionAndRotation: Detected suspicious (0, oldX, 0) transform. OldPos: {_simulatedPosition}, NewPos: {newPosition}.");
            }

            _simulatedPosition = newPosition;
            _simulatedRotation = newRotation;

            if (posChanged) PositionChanged?.Invoke(_simulatedPosition);
            if (rotChanged) RotationChanged?.Invoke(_simulatedRotation);
        }

        protected virtual void CleanupEvents() {
            OnDestroyed = null; OnLoudDestructionSignaled = null; PositionChanged = null; RotationChanged = null;
            _clientFloatingBehavior = null;
        }
        protected abstract void DeserializeSpecificInitialState(BinaryReader reader);
        protected abstract void DeserializeSpecificState(BinaryReader reader);
        protected abstract void HandleSpecificEvent(byte specificEventType, BinaryReader reader);
        protected abstract void InvokeSpecificStateChangedEvents();
    }
}