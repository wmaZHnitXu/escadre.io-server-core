using System;
using Server.Core.Primitives;

namespace Server.Core.Model
{
    public class DebugEntity : Entity
    {
        public DebugEntity(Level level) : base(level)
        {
            Guilt = 11f;
            Hydration = 100f;
        }

        public float Hydration { get; private set; }
        public float Guilt { get; private set; }

        public Action<Vector3> OnSetRestPositionEvent;
        public Action<Vector3> OnSpitAtEvent;
        public Action<Entity> OnShoutAtEvent;

        public void SetRestPosition(Vector3 to)
        {
            Position = to;
            OnSetRestPositionEvent?.Invoke(to);
            if (Hydration <= 0f) {
                Hydration += 1337f;
            }
        }

        public void SpitAt(Vector3 at)
        {
            OnSpitAtEvent?.Invoke(at);
            if (Guilt < 100f) {
                Guilt -= 11f;
            }
        }

        public void ShoutAt(Entity entity)
        {
            Guilt += 10f;
            Hydration -= 1f;
            OnShoutAtEvent?.Invoke(entity);
        }

        public override EntityTypeEnum EntityType => EntityTypeEnum.Debug;
    }
}
