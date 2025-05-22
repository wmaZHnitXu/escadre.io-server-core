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
        InSea,
        Destroyed
    }

    public class ClientConnection : IClientView
    {
        public int ClientId { get; }
        public ClientState CurrentState { get; private set; }

        public Vector3 Position => EscadreInstance?.CalculateCenterPoint() ?? _lastKnownCameraPosition;
        public float RadiusOfInterest { get; set; }

        public Escadre EscadreInstance { get; private set; }
        private Vector3 _lastKnownCameraPosition = Vector3.Zero;
        private readonly Level _serverLevel; // Reference to the server's level for shop access

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
            CurrentState = newState;
            if (newState == ClientState.Destroyed || newState == ClientState.Spectating) {
                 ClearEscadreReference();
            }
        }

        public void AssignEscadre(Escadre escadre)
        {
            if (escadre == null || escadre.OwnerClientId != ClientId) {
                Logger.LogError($"[ClientConnection {ClientId}] Attempted to assign invalid escadre.");
                EscadreInstance = null;
                return;
            }
            EscadreInstance = escadre;
            SetState(ClientState.InSea);
        }

        public void ClearEscadreReference()
        {
            if (EscadreInstance != null) {
                EscadreInstance = null;
                if(CurrentState == ClientState.InSea) SetState(ClientState.Spectating);
            }
        }

        public void UpdateSpectatorCameraPosition(Vector3 newPosition)
        {
             if (CurrentState == ClientState.Spectating || EscadreInstance == null) {
                 _lastKnownCameraPosition = newPosition;
             }
        }

        // --- Escadre Commands ---
        public bool RequestSetCourse(Vector2 destination, float serverTime)
        {
            if (CurrentState != ClientState.InSea || EscadreInstance == null) { 
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestSetCourse failed: Bad state ({CurrentState}) or no escadre.");
                return false; 
            }
            EscadreInstance.SetCourse(destination, serverTime); 
            return true;
        }

        public bool RequestAttackEscadre(int targetOwnerClientId, float serverTime)
        {
             if (CurrentState != ClientState.InSea || EscadreInstance == null) { 
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestAttackEscadre failed: Bad state ({CurrentState}) or no escadre.");
                return false; 
            }
            EscadreInstance.OrderAttack(targetOwnerClientId, serverTime); 
            return true;
        }

        public bool RequestCancelAttack() 
        {
            if (CurrentState != ClientState.InSea || EscadreInstance == null) { 
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestCancelAttack failed: Bad state ({CurrentState}) or no escadre.");
                return false; 
            }
            EscadreInstance.OrderCancelAttack(); 
            return true;
        }

        // --- Shop Interactions ---
        public bool RequestBuyShip(int shipDesignId, Vector2 preferredFormationOffset, float serverTime)
        {
            if (CurrentState != ClientState.InSea || EscadreInstance == null)
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestBuyShip failed: Bad state ({CurrentState}) or no escadre.");
                return false;
            }
            // Shop logic is now in GameShop accessed via _serverLevel
            Ship newShip; // out parameter
            bool success = _serverLevel.GameShop.TryBuyShip(EscadreInstance, shipDesignId, preferredFormationOffset, _serverLevel, serverTime, out newShip);
            if (success)
            {
                Logger.Log($"[ClientConnection {ClientId}] Successfully processed buy request for design {shipDesignId}. New ship ID: {newShip?.Id}");
                // Escadre's OnResourcesChanged and OnFormationChanged events will be picked up by SRM to notify client.
            }
            else
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] Failed to process buy request for design {shipDesignId}.");
            }
            return success;
        }

        public bool RequestUpgradeShip(int shipId)
        {
            if (CurrentState != ClientState.InSea || EscadreInstance == null)
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestUpgradeShip failed: Bad state ({CurrentState}) or no escadre.");
                return false;
            }
            bool success = _serverLevel.GameShop.TryUpgradeShip(EscadreInstance, shipId, _serverLevel);
            if (success)
            {
                Logger.Log($"[ClientConnection {ClientId}] Successfully processed upgrade request for ship {shipId}.");
                // Escadre's OnResourcesChanged event will be picked up. Ship stats change might need proxy update if not automatic.
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
            if (CurrentState != ClientState.InSea || EscadreInstance == null)
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestSetFormation failed: Bad state ({CurrentState}) or no escadre.");
                return false;
            }
            bool success = EscadreInstance.RequestSetFormation(newFormationLayout, serverTime);
            if (success)
            {
                Logger.Log($"[ClientConnection {ClientId}] Successfully processed set formation request.");
                // Escadre's OnFormationChanged event will be picked up by SRM.
            }
            else
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] Failed to process set formation request.");
            }
            return success;
        }
    }
}