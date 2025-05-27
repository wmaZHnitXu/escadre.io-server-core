// File: Core/Network/Proxies/ServerProxyFactory.cs
using System;
using Core.Logging;
using Core.Model;
using Core.Network;

namespace Core.Network.Proxies
{
    public static class ServerProxyFactory
    {
        public static IServerProxy CreateServerProxy(Entity entity, IServerNetworkLayer networkLayer)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            switch (entity.EntityType)
            {
                case Entity.EntityTypeEnum.Debug:
                    return new DebugEntityProxy.ServerProxy(entity as DebugEntity, networkLayer);

                case Entity.EntityTypeEnum.DefaultShip:
                    return new ShipProxy.ServerProxy(entity as Ship, networkLayer);
                
                case Entity.EntityTypeEnum.Escadre: 
                    return new EscadreProxy.ServerProxy(entity as Escadre, networkLayer);

                case Entity.EntityTypeEnum.ResourceBox: // Added
                    return new CollectableFloatingEntityProxy.ServerProxy<ResourceBox>(entity as ResourceBox, networkLayer);

                // Removed DefaultCannon from here as it's an AttachedEntity and typically not directly replicated
                // unless it needs its own independent PVS updates, which is unusual for a cannon.
                // If cannons were standalone turrets, they'd be here.
                // case Entity.EntityTypeEnum.DefaultCannon: 
                //    return new CannonProxy.ServerProxy(entity as DefaultCannon, networkLayer); // Example if DefaultCannon had own proxy

                default:
                    Logger.LogWarning($"[ServerProxyFactory] No ServerProxy registered for EntityType: {entity.EntityType}. Entity ID: {entity.Id}");
                    if (entity is Ship shipEntity) {
                        Logger.LogWarning($"[ServerProxyFactory] EntityType {entity.EntityType} is a Ship. Using ShipProxy as fallback.");
                        return new ShipProxy.ServerProxy(shipEntity, networkLayer);
                    }
                    if (entity is DestructibleEntity de) {
                        Logger.LogWarning($"[ServerProxyFactory] EntityType {entity.EntityType} is Destructible. Using DestructibleEntityProxy as fallback.");
                        return new DestructibleEntityProxy.ServerProxy<DestructibleEntity>(de, networkLayer);
                    }
                    if (entity is Escadre escadreEntityFallBack) {
                         Logger.LogError($"[ServerProxyFactory] EntityType {entity.EntityType} is Escadre but missed explicit case. Using EscadreProxy. THIS IS A BUG IN FACTORY.");
                        return new EscadreProxy.ServerProxy(escadreEntityFallBack, networkLayer);
                    }
                    // Fallback for other CollectableFloatingEntity types if not ResourceBox
                    if (entity is CollectableFloatingEntity cfe) {
                        Logger.LogWarning($"[ServerProxyFactory] EntityType {entity.EntityType} is CollectableFloatingEntity. Using generic CollectableFloatingEntityProxy.ServerProxy.");
                        return new CollectableFloatingEntityProxy.ServerProxy<CollectableFloatingEntity>(cfe, networkLayer);
                    }

                    throw new ArgumentException($"No ServerProxy registered or suitable base proxy found for EntityType: {entity.EntityType}");
            }
        }
    }
}