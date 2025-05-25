// File: Core/Network/NetworkTypes.cs
using System;
using System.IO;
using System.Collections.Generic;
using Core.Model;
using Core.Primitives;
using Core.Client; 

namespace Core.Network
{
    public enum MessageType : byte
    {
        // S->C Lifecycle & State
        CreateEntity = 1,
        DestroyEntity = 2,          
        VanishEntity = 3,           
        UpdateState = 4,            
        EntityEvent = 5,   
        OceanInitializationData = 6, 

        // C->S State Sync & View Updates
        _ClientSyncState = 10,
        _UpdateViewPosition = 11, 

        // C->S Player Commands (Escadre General)
        _SetCourse = 20,            
        _AttackEscadre = 21,        
        _CancelAttack = 22,         
        
        // C->S Player Commands (Shop & Formation) - Context: Client's Own Escadre
        _RequestBuyShip = 24,       
        _RequestUpgradeShip = 25,   
        _RequestSetFormation = 26,  

        // C->S Session Management
        _ClientConnectRequest = 30,
        _ClientDisconnect = 31,
    }

    public interface IClientProxy 
    {
        int EntityId { get; }
        Entity.EntityTypeEnum EntityType { get; }
        Vector3 Position { get; } 
        Quaternion Rotation { get; } 
        ClientLevel OwningClientLevel { get; } 

        void HandleNetworkMessage(MessageType messageType, BinaryReader reader);
        void NotifyDestroyed(); 
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
        void SendToClient(int targetGameClientId, int contextEntityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendVanishCommand(int entityId, IEnumerable<int> targetClientIds); 
        event Action<int /*sendingNetworkSourceId*/, int /*entityId_or_ContextId*/, MessageType, BinaryReader> OnClientMessageReceived;
    }

    public interface IClientNetworkLayer
    {
        void SendToServer(int sendingNetworkSourceId, int contextEntityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        event Action<int /*contextEntityId*/, MessageType, byte[] /*payload*/> OnMessageReceived;
    }
}