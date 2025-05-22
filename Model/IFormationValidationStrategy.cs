// File: Core/Model/IFormationValidationStrategy.cs
using Core.Primitives;
using System.Collections.Generic; // Required for IReadOnlyList

namespace Core.Model
{
    public interface IFormationValidationStrategy
    {
        /// <summary>
        /// Checks if a single proposed offset for a ship (or an empty slot) is valid within the given formation.
        /// </summary>
        /// <param name="formation">The current state of the formation (excluding the proposed change if modifying existing).</param>
        /// <param name="slots">All current slots in the formation, to check against.</param>
        /// <param name="proposedOffset">The new relative offset being tested.</param>
        /// <param name="forShipId">The ID of the ship this offset is for (null if it's a new, empty slot being considered).</param>
        /// <param name="reason">Output parameter for the reason if invalid.</param>
        /// <returns>True if the position is valid, false otherwise.</returns>
        bool IsValidPosition(IReadOnlyList<FormationSlot> slots, Vector2 proposedOffset, int? forShipId, out string reason);

        /// <summary>
        /// Checks if the entire formation configuration is valid.
        /// </summary>
        /// <param name="slots">All current slots in the formation.</param>
        /// <param name="reason">Output parameter for the reason if invalid.</param>
        /// <returns>True if the formation is valid, false otherwise.</returns>
        bool IsValidFormation(IReadOnlyList<FormationSlot> slots, out string reason);

        /// <summary>
        /// Suggests an initial valid offset for a new ship, if possible.
        /// </summary>
        /// <param name="slots">All current slots in the formation.</param>
        /// <param name="suggestedOffset">Output parameter for the suggested offset.</param>
        /// <returns>True if a suggestion could be made, false otherwise.</returns>
        bool TrySuggestInitialOffset(IReadOnlyList<FormationSlot> slots, out Vector2 suggestedOffset);
    }
}