// File: Core/Model/ResourceBox.cs
using Core.Primitives;
using Core.Logging;
using Core.Ocean;

namespace Core.Model
{
    public class ResourceBox : CollectableFloatingEntity
    {
        public override EntityTypeEnum EntityType => EntityTypeEnum.ResourceBox;

        public int ResourceAmount { get; private set; }

        // Note: FinalCollectionDistance and SuckSpeed are inherited and set in base constructor

        public ResourceBox(
            Level level, 
            Vector3 initialPosition, 
            int resourceAmount, 
            float finalCollectionDistance = 0.75f, 
            float suckSpeed = 4.0f)
            : base(level, initialPosition, finalCollectionDistance, suckSpeed)
        {
            ResourceAmount = resourceAmount > 0 ? resourceAmount : 10; // Ensure positive amount

            // Initialize floating behavior
            if (level.OceanDataProvider != null)
            {
                this.FloatingBehavior = new DefaultFloatingBehavior(
                    oceanDataProvider: level.OceanDataProvider,
                    buoyancyFactor: 1.0f,
                    rollInfluence: 0.2f,
                    pitchInfluence: 0.2f,
                    horizontalInfluence: 0.1f,
                    verticalInterpolationSpeed: 1.5f,
                    rotationalInterpolationSpeed: 20.0f
                );
            }
            else
            {
                Logger.LogWarning($"[ResourceBox ID:{this.Id}] OceanDataProvider not available. FloatingBehavior not initialized.");
            }

            // Logger.Log($"[ResourceBox ID pending:{this.Id}] Created. Amount: {ResourceAmount}, CollectionDist: {FinalCollectionDistance}, SuckSpeed: {SuckSpeed}");
        }

        protected override void ApplyPickupEffect(Ship collectingShip)
        {
            if (collectingShip == null || collectingShip.OwningEscadre == null)
            {
                Logger.LogWarning($"[ResourceBox {Id}] ApplyPickupEffect: Collecting ship or its escadre is null.");
                return;
            }

            Escadre ownerEscadre = collectingShip.OwningEscadre;
            ownerEscadre.AddResources(this.ResourceAmount);

            Logger.Log($"[ResourceBox {Id}] Granted {ResourceAmount} resources to Escadre {ownerEscadre.Id} (Owner: {ownerEscadre.OwnerClientId}). Escadre now has {ownerEscadre.Resources} resources.");
        }
    }
}