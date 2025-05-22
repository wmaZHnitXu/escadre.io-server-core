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
            DeserializeSpecificInitialState(reader);

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
            // Try to interpret as BaseProxyEventType first
            if (Enum.IsDefined(typeof(BaseProxyEventType), eventTypeByte))
            {
                HandleBaseProxyEvent((BaseProxyEventType)eventTypeByte, reader);
            }
            else
            {
                // If not a base event, pass to derived proxy's specific event handler
                HandleSpecificEvent(eventTypeByte, reader);
            }
        }
        
        // Method made public to match accessibility of BaseProxyEventType if it were protected internal
        // but since BaseProxyEventType is now public, this can remain protected.
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

            // User observation: Original (X, Y, Z) might become (0, X, 0)
            // Check if newX and newZ are close to zero, and newY is close to the old _simulatedPosition.X
            if (Math.Abs(newPosition.X) < 0.01f && 
                Math.Abs(newPosition.Z) < 0.01f &&
                _simulatedPosition != null && // Ensure _simulatedPosition has been initialized
                Math.Abs(newPosition.Y - _simulatedPosition.X) < 0.01f &&
                (_simulatedPosition.X != 0f || Math.Abs(newPosition.Y) > 0.01f) && // Avoid trivial (0,0,0) from (0,Y,Z) old pos, ensure oldX or newY is significant
                newPosition != _simulatedPosition) // Log only if it's a change to this specific pattern
            {
                Logger.LogWarning($"[BaseClientProxy {EntityId}] SetSimulatedPositionAndRotation: Detected suspicious (0, oldX, 0) transform. OldPos: {_simulatedPosition}, NewPos: {newPosition}. Stack: {Environment.StackTrace}");
            }

            _simulatedPosition = newPosition;
            _simulatedRotation = newRotation;

            if (posChanged) PositionChanged?.Invoke(_simulatedPosition);
            if (rotChanged) RotationChanged?.Invoke(_simulatedRotation);
        }

        protected virtual void CleanupEvents() {
            OnDestroyed = null; OnLoudDestructionSignaled = null; PositionChanged = null; RotationChanged = null;
        }
        protected abstract void DeserializeSpecificInitialState(BinaryReader reader);
        protected abstract void DeserializeSpecificState(BinaryReader reader);
        protected abstract void HandleSpecificEvent(byte specificEventType, BinaryReader reader);
        protected abstract void InvokeSpecificStateChangedEvents();
    }
}