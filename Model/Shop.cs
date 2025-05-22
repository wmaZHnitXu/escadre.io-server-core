// File: Core/Model/Shop.cs
using System.Collections.Generic;
using System.Linq;
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    public class Shop
    {
        private readonly List<ShipDesign> _availableShipDesigns = new List<ShipDesign>();
        public IReadOnlyList<ShipDesign> AvailableShipDesigns => _availableShipDesigns.AsReadOnly();

        public Shop(List<ShipDesign> initialDesigns)
        {
            if (initialDesigns != null)
            {
                _availableShipDesigns.AddRange(initialDesigns);
            }
        }

        public ShipDesign GetDesignById(int designId)
        {
            return _availableShipDesigns.FirstOrDefault(d => d.DesignId == designId);
        }

        public bool TryBuyShip(Escadre buyerEscadre, int shipDesignId, Vector2 formationOffset, Level level, float serverTime, out Ship newShip)
        {
            newShip = null;
            if (buyerEscadre == null) { Logger.LogError("[Shop] BuyerEscadre is null."); return false; }
            if (level == null) { Logger.LogError("[Shop] Level is null."); return false; }

            ShipDesign designToBuy = GetDesignById(shipDesignId);
            if (designToBuy == null)
            {
                Logger.LogWarning($"[Shop] ShipDesign ID {shipDesignId} not found.");
                return false;
            }

            if (!buyerEscadre.DeductResources(designToBuy.Cost))
            {
                Logger.LogWarning($"[Shop Escadre {buyerEscadre.OwnerClientId}] Not enough resources to buy {designToBuy.Name}. Need {designToBuy.Cost}, Has {buyerEscadre.Resources}.");
                return false;
            }

            Logger.Log($"[Shop Escadre {buyerEscadre.OwnerClientId}] Buying ship {designToBuy.Name} for {designToBuy.Cost}. Resources left: {buyerEscadre.Resources}");

            // For now, spawn position is determined by escadre center + formation offset
            // A more advanced system might have designated spawn points or allow client to pick near escadre.
            Vector3 escadreCenter = buyerEscadre.CalculateCenterPoint();
            // Assuming formation offset is relative to a non-rotated escadre for spawn,
            // or that ships orient themselves quickly.
            Vector3 spawnPosition = escadreCenter + new Vector3(formationOffset.X, 0, formationOffset.Y);

            // Create the actual ship entity based on the design's entity type
            switch (designToBuy.ShipEntityType)
            {
                case Entity.EntityTypeEnum.DefaultShip:
                    newShip = new DefaultShip(level, buyerEscadre, spawnPosition);
                    break;
                // Add cases for other ship types defined in ShipDesign
                // case Entity.EntityTypeEnum.Frigate:
                // newShip = new Frigate(level, buyerEscadre, spawnPosition); // Assuming Frigate class exists
                // break;
                default:
                    Logger.LogError($"[Shop] Unhandled ShipEntityType {designToBuy.ShipEntityType} for design {designToBuy.Name}. Cannot create ship.");
                    // Refund resources if creation fails critically after deduction
                    buyerEscadre.AddResources(designToBuy.Cost); // Refund
                    return false;
            }

            // The ship's constructor already adds it to the Level.
            // Now add it to the escadre and its formation.
            // The ship.Id will be assigned when AddEntity calls AssignId.
            // So, AddShip must be called after the ship is added to the Level's _toAdd list
            // and its ID is available. This implies a slight refactor or careful ordering.
            // For now, assume ID is set when new DefaultShip() is called (which calls Level.AddEntity -> AssignId).
            // This works because AddEntity in Level assigns ID immediately, before _toAdd is processed.

            buyerEscadre.AddShip(newShip, formationOffset); // AddShip also handles adding to formation
            
            // After adding the ship, the escadre should update its movement targets
            // so the new ship (and existing ones) move to their formation spots.
            buyerEscadre.UpdateShipMovementTargets(serverTime);

            Logger.Log($"[Shop Escadre {buyerEscadre.OwnerClientId}] Successfully bought and added Ship ID {newShip.Id} ({designToBuy.Name}).");
            return true;
        }

        public bool TryUpgradeShip(Escadre buyerEscadre, int shipIdToUpgrade, Level level)
        {
            if (buyerEscadre == null) { Logger.LogError("[Shop] BuyerEscadre is null for upgrade."); return false; }
            if (level == null) { Logger.LogError("[Shop] Level is null for upgrade."); return false; }

            if (!level.TryGetEntity(shipIdToUpgrade, out Entity entity) || !(entity is Ship ship) || ship.IsDead)
            {
                Logger.LogWarning($"[Shop Escadre {buyerEscadre.OwnerClientId}] Ship ID {shipIdToUpgrade} not found, not a ship, or is dead. Cannot upgrade.");
                return false;
            }

            if (ship.OwningEscadreClientId != buyerEscadre.OwnerClientId)
            {
                 Logger.LogWarning($"[Shop Escadre {buyerEscadre.OwnerClientId}] Attempted to upgrade ship {shipIdToUpgrade} not owned by this escadre.");
                return false;
            }

            int upgradeCost = buyerEscadre.GetUpgradeCostForShip(ship); // Cost calculation can be on Escadre or Ship

            if (!buyerEscadre.DeductResources(upgradeCost))
            {
                Logger.LogWarning($"[Shop Escadre {buyerEscadre.OwnerClientId}] Not enough resources to upgrade Ship {shipIdToUpgrade}. Need {upgradeCost}, Have {buyerEscadre.Resources}.");
                return false;
            }

            Logger.Log($"[Shop Escadre {buyerEscadre.OwnerClientId}] Upgrading Ship {shipIdToUpgrade}. Cost: {upgradeCost}. Res left: {buyerEscadre.Resources}.");
            ship.PerformUpgrade();
            // After upgrade, stats might change, potentially affecting proxy data.
            // The proxy mechanism should pick up these changes if it serializes relevant stats.
            // No explicit call to UpdateShipMovementTargets needed unless upgrade changes speed/role significantly.
            return true;
        }
    }
}