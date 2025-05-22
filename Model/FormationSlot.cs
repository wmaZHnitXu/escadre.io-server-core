// File: Core/Model/FormationSlot.cs
using Core.Primitives;

namespace Core.Model
{
    public class FormationSlot
    {
        public int? ShipEntityId { get; set; } // Nullable if the slot is empty
        public Vector2 RelativeOffset { get; set; } // Relative to Escadre center/anchor

        public FormationSlot(Vector2 relativeOffset, int? shipEntityId = null)
        {
            RelativeOffset = relativeOffset;
            ShipEntityId = shipEntityId;
        }
    }
}