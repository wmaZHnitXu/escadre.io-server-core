// File: Core/Network/NetworkTypes.cs
using System;
using System.IO;
using System.Collections.Generic;
using Core.Model;
using Core.Primitives;
using Core.Client; // For ClientLevel reference in IClientProxy constructor (conceptually)

namespace Core.Network
{
    public enum MessageType : byte
    {
        // S->C Lifecycle & State
        CreateEntity = 1,
        DestroyEntity = 2,          // Loud Destruction signal
        VanishEntity = 3,           // Silent Removal / PVS exit signal
        UpdateState = 4,            // Full authoritative state correction from server
        EntityEvent = 5,            // For specific gameplay events (e.g., TookDamageVisual, SetMovementTarget)

        // C->S State Sync & View Updates
        _ClientSyncState = 10,
        _UpdateViewPosition = 11,

        // C->S Player Commands
        _SetCourse = 20,
        _AttackEscadre = 21,
        _CancelAttack = 22,
        _UpgradeShip = 23,
        _BuyShip = 24,
    }

    public interface IClientProxy
    {
        int EntityId { get; }
        Entity.EntityTypeEnum EntityType { get; }
        Vector3 Position { get; }
        Quaternion Rotation { get; }
        ClientLevel OwningClientLevel { get; } // Added to access time or other level context

        void HandleNetworkMessage(MessageType messageType, BinaryReader reader);
        void NotifyDestroyed();
        // Update signature changed: no longer takes clientSimulatedServerTime
        void Update(float deltaTime); 

        event Action OnDestroyed;
        event Action OnLoudDestructionSignaled;
        event Action<Vector3> PositionChanged;
        event Action<Quaternion> RotationChanged;
    }

    public interface IServerProxy
    {
        int EntityId { get; }
        Entity.EntityTypeEnum EntityType { get; }
        Vector3 Position { get; }
        Quaternion Rotation { get; }
        void StartReplicating();
        void StopReplicating();
        bool CheckClientSyncState(BinaryReader reader);
        void SerializeSpecificInitialState(BinaryWriter writer);
        void SerializeCorrectionState(BinaryWriter writer);
        void SendVanishMessage(IEnumerable<int> targetClientIds);
    }

    public interface IServerNetworkLayer
    {
        void BroadcastRelevant(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendToClient(int clientId, int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendVanishCommand(int entityId, IEnumerable<int> targetClientIds);
        event Action<int, int, MessageType, BinaryReader> OnClientMessageReceived;
    }

    public interface IClientNetworkLayer
    {
        void SendToServer(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        event Action<int, MessageType, BinaryReader> OnMessageReceived;
    }
}