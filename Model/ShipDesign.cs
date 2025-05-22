// File: Core/Model/ShipDesign.cs
using Core.Primitives; // If designs store base stats like default speed, range etc.

namespace Core.Model
{
    public class ShipDesign
    {
        public int DesignId { get; }
        public string Name { get; }
        public int Cost { get; }
        public Entity.EntityTypeEnum ShipEntityType { get; } // e.g., DefaultShip, Frigate

        // Optional: Could include base stats if designs are more varied than just entity type
        // public float BaseMaxSpeed { get; }
        // public float BaseAttackRange { get; }

        public ShipDesign(int designId, string name, int cost, Entity.EntityTypeEnum shipEntityType)
        {
            DesignId = designId;
            Name = name ?? throw new System.ArgumentNullException(nameof(name));
            Cost = cost;
            ShipEntityType = shipEntityType; // Could validate if this is a valid Ship type
        }
    }
}