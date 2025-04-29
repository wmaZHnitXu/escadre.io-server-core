using Server.Core.Model;
using Server.Core.Primitives;

public class DebugEntity : Entity
{
    public DebugEntity(Level level, Vector3 spawnPoint) : base(level)
    {
        Position = spawnPoint;
    }

    public override EntityTypeEnum EntityType => EntityTypeEnum.Debug;
}
