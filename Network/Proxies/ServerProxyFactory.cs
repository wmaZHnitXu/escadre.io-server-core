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
                    // DefaultShip IS A Ship, which IS A DestructibleEntity.
                    // We use ShipProxy which handles both Ship and DestructibleEntity aspects.
                    return new ShipProxy.ServerProxy(entity as Ship, networkLayer);
                // Add cases for other concrete entity types:
                // case Entity.EntityTypeEnum.Frigate:
                //    return new ShipProxy.ServerProxy(entity as Ship, networkLayer); // Or a FrigateProxy if it has more specific needs
                // case Entity.EntityTypeEnum.SomeDestructibleTerrain: // Example of non-Ship Destructible
                //    return new DestructibleEntityProxy.ServerProxy<DestructibleEntity>(entity as DestructibleEntity, networkLayer);

                default:
                    Logger.LogWarning($"[ServerProxyFactory] No ServerProxy registered for EntityType: {entity.EntityType}. Entity ID: {entity.Id}");
                    // Fallback or error for unhandled DestructibleEntity types if any exist that are not ships
                    if (entity is Ship shipEntity) { // If it's some unknown Ship type, use generic ShipProxy
                        Logger.LogWarning($"[ServerProxyFactory] EntityType {entity.EntityType} is a Ship. Using ShipProxy.");
                        return new ShipProxy.ServerProxy(shipEntity, networkLayer);
                    }
                    if (entity is DestructibleEntity de) { // If it's some unknown DestructibleEntity, use generic DE proxy
                        Logger.LogWarning($"[ServerProxyFactory] EntityType {entity.EntityType} is Destructible. Using DestructibleEntityProxy.");
                        return new DestructibleEntityProxy.ServerProxy<DestructibleEntity>(de, networkLayer);
                    }
                    throw new ArgumentException($"No ServerProxy registered or suitable base proxy found for EntityType: {entity.EntityType}");
            }
        }
    }
}