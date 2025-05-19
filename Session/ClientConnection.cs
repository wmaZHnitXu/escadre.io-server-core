// File: Core/Session/ClientConnection.cs
using System;
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

        public ClientConnection(int clientId, float initialRadiusOfInterest = 100f)
        {
            ClientId = clientId;
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

        public bool RequestUpgradeShip(int shipId) 
        {
             if (CurrentState != ClientState.InSea || EscadreInstance == null) { 
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestUpgradeShip failed: Bad state ({CurrentState}) or no escadre.");
                return false; 
            }
            EscadreInstance.RequestUpgradeShip(shipId);
            return true;
        }

        // Signature updated to include spawnPosition
        public bool RequestBuyShip(int shipDesignId, Vector3 spawnPosition, float serverTime) 
        {
             if (CurrentState != ClientState.InSea || EscadreInstance == null) { 
                Logger.LogWarning($"[ClientConnection {ClientId}] RequestBuyShip failed: Bad state ({CurrentState}) or no escadre.");
                return false; 
            }
            // This will now call the Escadre.RequestBuyShip that throws NotImplementedException
            return EscadreInstance.RequestBuyShip(shipDesignId, spawnPosition, serverTime); 
        }
    }
}