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
            if (buyerEscadre.IsDead) { Logger.LogWarning($"[Shop] BuyerEscadre {buyerEscadre.Id} is dead. Cannot buy ship."); return false; }
            if (level == null) { Logger.LogError("[Shop] Level is null."); return false; }

            ShipDesign designToBuy = GetDesignById(shipDesignId);
            if (designToBuy == null)
            {
                Logger.LogWarning($"[Shop] ShipDesign ID {shipDesignId} not found.");
                return false;
            }

            if (!buyerEscadre.DeductResources(designToBuy.Cost))
            {
                Logger.LogWarning($"[Shop Escadre {buyerEscadre.Id} (Owner {buyerEscadre.OwnerClientId})] Not enough resources to buy {designToBuy.Name}. Need {designToBuy.Cost}, Has {buyerEscadre.Resources}.");
                return false;
            }

            Logger.Log($"[Shop Escadre {buyerEscadre.Id} (Owner {buyerEscadre.OwnerClientId})] Buying ship {designToBuy.Name} for {designToBuy.Cost}. Resources left: {buyerEscadre.Resources}");

            // Spawn position is based on the Escadre entity's position (its anchor) + formation offset relative to escadre's rotation
            Vector3 spawnPosition = buyerEscadre.Position + (buyerEscadre.Rotation * new Vector3(formationOffset.X, 0, formationOffset.Y));


            switch (designToBuy.ShipEntityType)
            {
                case Entity.EntityTypeEnum.DefaultShip:
                    newShip = new DefaultShip(level, buyerEscadre, spawnPosition);
                    break;
                default:
                    Logger.LogError($"[Shop] Unhandled ShipEntityType {designToBuy.ShipEntityType} for design {designToBuy.Name}. Cannot create ship.");
                    buyerEscadre.AddResources(designToBuy.Cost); 
                    return false;
            }
            // Ship constructor calls level.AddEntity()

            buyerEscadre.AddShip(newShip, formationOffset); 
            buyerEscadre.UpdateShipMovementTargets(serverTime);

            Logger.Log($"[Shop Escadre {buyerEscadre.Id} (Owner {buyerEscadre.OwnerClientId})] Successfully bought and added Ship ID {newShip.Id} ({designToBuy.Name}).");
            return true;
        }

        public bool TryUpgradeShip(Escadre buyerEscadre, int shipIdToUpgrade, Level level)
        {
            if (buyerEscadre == null) { Logger.LogError("[Shop] BuyerEscadre is null for upgrade."); return false; }
            if (buyerEscadre.IsDead) { Logger.LogWarning($"[Shop] BuyerEscadre {buyerEscadre.Id} is dead. Cannot upgrade ship."); return false; }
            if (level == null) { Logger.LogError("[Shop] Level is null for upgrade."); return false; }

            if (!level.TryGetEntity(shipIdToUpgrade, out Entity entity) || !(entity is Ship ship) || ship.IsDead)
            {
                Logger.LogWarning($"[Shop Escadre {buyerEscadre.Id}] Ship ID {shipIdToUpgrade} not found, not a ship, or is dead. Cannot upgrade.");
                return false;
            }

            if (ship.OwningEscadreClientId != buyerEscadre.OwnerClientId) // Or check ship.OwningEscadre.Id == buyerEscadre.Id
            {
                 Logger.LogWarning($"[Shop Escadre {buyerEscadre.Id}] Attempted to upgrade ship {shipIdToUpgrade} not owned by this escadre.");
                return false;
            }

            int upgradeCost = buyerEscadre.GetUpgradeCostForShip(ship); 

            if (!buyerEscadre.DeductResources(upgradeCost))
            {
                Logger.LogWarning($"[Shop Escadre {buyerEscadre.Id}] Not enough resources to upgrade Ship {shipIdToUpgrade}. Need {upgradeCost}, Have {buyerEscadre.Resources}.");
                return false;
            }

            Logger.Log($"[Shop Escadre {buyerEscadre.Id}] Upgrading Ship {shipIdToUpgrade}. Cost: {upgradeCost}. Res left: {buyerEscadre.Resources}.");
            ship.PerformUpgrade();
            return true;
        }
    }
}