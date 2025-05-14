// File: Scripts/Server/Core/Model/DamageInfo.cs
using Core.Primitives;

namespace Core.Model
{
    public enum DamageType
    {
        Kinetic,
        Energy,
        Explosive,
        Corrosive,
        Internal // e.g., self-destruct or internal failure
    }

    public struct DamageInfo
    {
        public float Amount;
        public DamageType Type;
        public Vector3 HitPoint;    // Point of impact in world space
        public Vector3 Direction;   // Direction from which the damage came
        public int? AttackerId;     // ID of the Entity that caused the damage, if applicable
        public int? AttackerOwnerClientId; // Client ID of the attacker's owner, if applicable

        public DamageInfo(float amount, DamageType type, Vector3 hitPoint, Vector3 direction, int? attackerId = null, int? attackerOwnerClientId = null)
        {
            Amount = amount;
            Type = type;
            HitPoint = hitPoint;
            Direction = direction;
            AttackerId = attackerId;
            AttackerOwnerClientId = attackerOwnerClientId;
        }
    }
}