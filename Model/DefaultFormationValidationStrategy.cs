// File: Core/Model/DefaultFormationValidationStrategy.cs
using Core.Primitives;
using System.Collections.Generic;
using System.Linq;

namespace Core.Model
{
    public class DefaultFormationValidationStrategy : IFormationValidationStrategy
    {
        public float MaxDistanceFromCenter { get; }
        public float MinDistanceBetweenShips { get; }

        public DefaultFormationValidationStrategy(float maxDistanceFromCenter = 20f, float minDistanceBetweenShips = 2f)
        {
            MaxDistanceFromCenter = maxDistanceFromCenter;
            MinDistanceBetweenShips = minDistanceBetweenShips;
        }

        public bool IsValidPosition(IReadOnlyList<FormationSlot> slots, Vector2 proposedOffset, int? forShipId, out string reason)
        {
            if (proposedOffset.SqrMagnitude > MaxDistanceFromCenter * MaxDistanceFromCenter)
            {
                reason = $"Position {proposedOffset} is too far from the center (max: {MaxDistanceFromCenter}).";
                return false;
            }

            foreach (var existingSlot in slots)
            {
                // If we are checking a position for an existing ship, don't compare it against its current self.
                if (forShipId.HasValue && existingSlot.ShipEntityId == forShipId)
                {
                    continue;
                }
                if (existingSlot.ShipEntityId.HasValue) // Only check against occupied slots for overlap
                {
                     if ((existingSlot.RelativeOffset - proposedOffset).SqrMagnitude < MinDistanceBetweenShips * MinDistanceBetweenShips)
                    {
                        reason = $"Position {proposedOffset} is too close to another ship at {existingSlot.RelativeOffset} (min dist: {MinDistanceBetweenShips}).";
                        return false;
                    }
                }
            }

            reason = "Position is valid.";
            return true;
        }

        public bool IsValidFormation(IReadOnlyList<FormationSlot> slots, out string reason)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                var slotA = slots[i];
                if (slotA.RelativeOffset.SqrMagnitude > MaxDistanceFromCenter * MaxDistanceFromCenter)
                {
                    reason = $"Ship/Slot at {slotA.RelativeOffset} is too far from center.";
                    return false;
                }

                for (int j = i + 1; j < slots.Count; j++)
                {
                    var slotB = slots[j];
                    // Only check occupied slots for collision with each other
                    if (slotA.ShipEntityId.HasValue && slotB.ShipEntityId.HasValue)
                    {
                        if ((slotA.RelativeOffset - slotB.RelativeOffset).SqrMagnitude < MinDistanceBetweenShips * MinDistanceBetweenShips)
                        {
                             reason = $"Ships/Slots at {slotA.RelativeOffset} and {slotB.RelativeOffset} are too close.";
                             return false;
                        }
                    }
                }
            }
            reason = "Formation is valid.";
            return true;
        }

        public bool TrySuggestInitialOffset(IReadOnlyList<FormationSlot> slots, out Vector2 suggestedOffset)
        {
            // Simple suggestion: try placing radially outwards.
            // This is a basic placeholder and can be much more sophisticated.
            int attempt = 0;
            float angleStep = 30f; // degrees
            float currentRadius = MinDistanceBetweenShips * 1.5f; // Start slightly further than min distance

            while(attempt < 36) // Try up to 36 positions (12 per radius increment)
            {
                for (int i = 0; i < 12; i++)
                {
                    float angle = i * angleStep * (System.MathF.PI / 180f);
                    Vector2 candidate = new Vector2(System.MathF.Cos(angle) * currentRadius, System.MathF.Sin(angle) * currentRadius);

                    if (IsValidPosition(slots, candidate, null, out _))
                    {
                        suggestedOffset = candidate;
                        return true;
                    }
                }
                currentRadius += MinDistanceBetweenShips; // Increase radius for next ring of attempts
                if(currentRadius > MaxDistanceFromCenter) break; // Don't suggest outside max range
                attempt++;
            }

            suggestedOffset = Vector2.Zero;
            return false; // Could not find a simple valid position
        }
    }
}