// File: Core/Session/ClientConnection.cs
using System;
using System.Collections.Generic; // Required for List
using Core.Model;
using Core.Primitives;
using Core.Visibility;
using Core.Logging;

namespace Core.Session
{
    public enum ClientState
    {
        Connecting,
        Spectating,
        InSea,      // Player controls an Escadre
        Destroyed   // Session terminated
    }

    public class ClientConnection : IClientView
    {
        public int ClientId { get; }
        public ClientState CurrentState { get; private set; }

        // Position for IClientView should be the Escadre's position if it exists.
        public Vector3 Position => EscadreEntity?.Position ?? _lastKnownSpectatorPosition;
        public float RadiusOfInterest { get; set; }

        public Escadre EscadreEntity { get; private set; } // Changed from EscadreInstance
        private Vector3 _lastKnownSpectatorPosition = Vector3.Zero;
        private readonly Level _serverLevel; 

        public ClientConnection(int clientId, Level serverLevel, float initialRadiusOfInterest = 100f)
        {
            ClientId = clientId;
            _serverLevel = serverLevel ?? throw new ArgumentNullException(nameof(serverLevel));
            RadiusOfInterest = initialRadiusOfInterest;
            CurrentState = ClientState.Connecting;
        }

        public void SetState(ClientState newState)
        {
            if (CurrentState == newState) return;
            Logger.Log($"[ClientConnection {ClientId}] State changing from {CurrentState} to {newState}");
            CurrentState = newState;
            if (newState == ClientState.Destroyed || newState == ClientState.Spectating) {
                 ClearEscadreReference(); // Ensure escadre is cleared if applicable
            }
        }

        public void AssignEscadre(Escadre escadreEntity) // Parameter is now Escadre (which is an Entity)
        {
            if (escadreEntity == null || escadreEntity.OwnerClientId != ClientId) {
                Logger.LogError($"[ClientConnection {ClientId}] Attempted to assign invalid escadre entity. Escadre Entity ID: {escadreEntity?.Id}, Escadre Owner: {escadreEntity?.OwnerClientId}");
                EscadreEntity = null;
                // If previously InSea, transition to Spectating
                if(CurrentState == ClientState.InSea) SetState(ClientState.Spectating);
                return;
            }
            EscadreEntity = escadreEntity;
            SetState(ClientState.InSea);
            Logger.Log($"[ClientConnection {ClientId}] Assigned Escadre Entity ID: {EscadreEntity.Id}");
        }

        public void ClearEscadreReference()
        {
            if (EscadreEntity != null) {
                Logger.Log($"[ClientConnection {ClientId}] Clearing Escadre reference (Entity ID: {EscadreEntity.Id}).");
                EscadreEntity = null;
                if(CurrentState == ClientState.InSea) SetState(ClientState.Spectating);
            }
        }

        public void UpdateSpectatorCameraPosition(Vector3 newPosition)
        {
             if (CurrentState == ClientState.Spectating || EscadreEntity == null) {
                 _lastKnownSpectatorPosition = newPosition;
             }
        }

        // --- Escadre Commands ---
        public bool RequestSetCourse(Vector2 destination, float serverTime)
        {
            if (CurrentState != ClientState.InSea || EscadreEntity == null || EscadreEntity.IsDead) { 
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestSetCourse failed: Bad state ({CurrentState}), no escadre, or escadre dead.");
                return false; 
            }
            EscadreEntity.SetCourse(destination, serverTime); 
            return true;
        }

        // Now takes targetEscadreEntityId
        public bool RequestAttackEscadre(int targetEscadreEntityId, float serverTime)
        {
             if (CurrentState != ClientState.InSea || EscadreEntity == null || EscadreEntity.IsDead) { 
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestAttackEscadre failed: Bad state ({CurrentState}), no escadre, or escadre dead.");
                return false; 
            }
            // The Escadre model's OrderAttackEscadreEntity method will validate the targetEscadreEntityId
            EscadreEntity.OrderAttackEscadreEntity(targetEscadreEntityId, serverTime); 
            return true;
        }

        public bool RequestCancelAttack() 
        {
            if (CurrentState != ClientState.InSea || EscadreEntity == null || EscadreEntity.IsDead) { 
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestCancelAttack failed: Bad state ({CurrentState}), no escadre, or escadre dead.");
                return false; 
            }
            EscadreEntity.OrderCancelAttack(); 
            return true;
        }

        // --- Shop Interactions ---
        public bool RequestBuyShip(int shipDesignId, Vector2 preferredFormationOffset, float serverTime)
        {
            if (CurrentState != ClientState.InSea || EscadreEntity == null || EscadreEntity.IsDead)
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestBuyShip failed: Bad state ({CurrentState}), no escadre, or escadre dead.");
                return false;
            }
            Ship newShip; 
            bool success = _serverLevel.GameShop.TryBuyShip(EscadreEntity, shipDesignId, preferredFormationOffset, _serverLevel, serverTime, out newShip);
            if (success)
            {
                Logger.Log($"[ClientConnection {ClientId}] Successfully processed buy request for design {shipDesignId}. New ship ID: {newShip?.Id}");
            }
            else
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] Failed to process buy request for design {shipDesignId}.");
            }
            return success;
        }

        public bool RequestUpgradeShip(int shipId)
        {
            if (CurrentState != ClientState.InSea || EscadreEntity == null || EscadreEntity.IsDead)
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestUpgradeShip failed: Bad state ({CurrentState}), no escadre, or escadre dead.");
                return false;
            }
            bool success = _serverLevel.GameShop.TryUpgradeShip(EscadreEntity, shipId, _serverLevel);
            if (success)
            {
                Logger.Log($"[ClientConnection {ClientId}] Successfully processed upgrade request for ship {shipId}.");
            }
            else
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] Failed to process upgrade request for ship {shipId}.");
            }
            return success;
        }

        // --- Formation Management ---
        public bool RequestSetFormation(List<Tuple<int, Vector2>> newFormationLayout, float serverTime)
        {
            if (CurrentState != ClientState.InSea || EscadreEntity == null || EscadreEntity.IsDead)
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestSetFormation failed: Bad state ({CurrentState}), no escadre, or escadre dead.");
                return false;
            }
            bool success = EscadreEntity.RequestSetFormation(newFormationLayout, serverTime);
            if (success)
            {
                Logger.Log($"[ClientConnection {ClientId}] Successfully processed set formation request.");
            }
            else
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] Failed to process set formation request.");
            }
            return success;
        }
    }
}