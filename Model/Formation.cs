// File: Core/Model/Formation.cs
using Core.Primitives;
using Core.Logging;
using System.Collections.Generic;
using System.Linq;
using System; // Required for ArgumentNullException

namespace Core.Model
{
    public class Formation
    {
        public int OwnerClientId { get; } // Or EscadreId
        private readonly List<FormationSlot> _slots = new List<FormationSlot>();
        public IReadOnlyList<FormationSlot> Slots => _slots.AsReadOnly();

        public IFormationValidationStrategy ValidationStrategy { get; set; }

        public event Action OnFormationLayoutChanged; // Event for structural changes

        public Formation(int ownerClientId, IFormationValidationStrategy initialStrategy)
        {
            OwnerClientId = ownerClientId;
            ValidationStrategy = initialStrategy ?? throw new ArgumentNullException(nameof(initialStrategy));
        }

        public bool TrySetShipRelativeOffset(int shipId, Vector2 newRelativeOffset, out string reason)
        {
            var slot = _slots.FirstOrDefault(s => s.ShipEntityId == shipId);
            if (slot == null)
            {
                reason = $"Ship ID {shipId} not found in formation.";
                return false;
            }

            // Create a temporary list of slots *without* the current ship's old position, to validate against.
            var otherSlots = _slots.Where(s => s.ShipEntityId != shipId).ToList();


            if (!ValidationStrategy.IsValidPosition(otherSlots, newRelativeOffset, shipId, out reason))
            {
                return false;
            }

            slot.RelativeOffset = newRelativeOffset;
            Logger.Log($"[Formation {OwnerClientId}] Ship {shipId} offset changed to {newRelativeOffset}.");
            OnFormationLayoutChanged?.Invoke();
            return true;
        }

        public bool TryAddShip(int shipId, Vector2 preferredOffset, out string reason)
        {
            if (_slots.Any(s => s.ShipEntityId == shipId))
            {
                reason = $"Ship ID {shipId} already in formation.";
                // Potentially update its offset if desired, but strict add should fail.
                // For now, fail.
                return false;
            }
            
            // Validate against existing slots
            if (!ValidationStrategy.IsValidPosition(_slots, preferredOffset, shipId, out reason))
            {
                // Try to get a suggested fallback position if preferred is invalid
                if (ValidationStrategy.TrySuggestInitialOffset(_slots, out Vector2 suggestedOffset))
                {
                     // Validate the suggested offset as well
                    if (!ValidationStrategy.IsValidPosition(_slots, suggestedOffset, shipId, out string suggestedReason))
                    {
                        reason = $"Preferred offset {preferredOffset} invalid ({reason}). Suggested offset {suggestedOffset} also invalid ({suggestedReason}).";
                        return false;
                    }
                    preferredOffset = suggestedOffset; // Use suggested offset
                    Logger.Log($"[Formation {OwnerClientId}] Preferred offset for ship {shipId} was invalid. Using suggested offset {preferredOffset}. Original reason: {reason}");
                    reason = $"Used suggested offset {preferredOffset}."; // Update reason to be more informative for success
                }
                else
                {
                    reason = $"Preferred offset {preferredOffset} invalid ({reason}). Could not suggest an alternative.";
                    return false;
                }
            }


            _slots.Add(new FormationSlot(preferredOffset, shipId));
            Logger.Log($"[Formation {OwnerClientId}] Ship {shipId} added to formation at {preferredOffset}.");
            OnFormationLayoutChanged?.Invoke();
            return true;
        }
        
        public bool AssignShipToAutoSlot(int shipId, out Vector2 assignedOffset)
        {
            assignedOffset = Vector2.Zero;
            if (_slots.Any(s => s.ShipEntityId == shipId))
            {
                Logger.LogWarning($"[Formation {OwnerClientId}] Ship ID {shipId} already in formation during auto-assign.");
                var existingSlot = _slots.First(s => s.ShipEntityId == shipId); // Should exist
                assignedOffset = existingSlot.RelativeOffset;
                return true;
            }

            if (ValidationStrategy.TrySuggestInitialOffset(_slots, out Vector2 suggestedOffset))
            {
                // IsValidPosition for auto-slotting should ideally check against all current slots for the new ship.
                if (ValidationStrategy.IsValidPosition(_slots, suggestedOffset, shipId, out _)) // forShipId is shipId to check against others
                {
                    _slots.Add(new FormationSlot(suggestedOffset, shipId));
                    Logger.Log($"[Formation {OwnerClientId}] Ship {shipId} auto-assigned to formation at {suggestedOffset}.");
                    assignedOffset = suggestedOffset;
                    OnFormationLayoutChanged?.Invoke();
                    return true;
                }
                 Logger.LogWarning($"[Formation {OwnerClientId}] Suggested offset {suggestedOffset} for ship {shipId} was invalid after all.");
            }
            
            Logger.LogWarning($"[Formation {OwnerClientId}] Could not auto-assign ship {shipId} to a valid formation slot.");
            return false;
        }


        public bool RemoveShip(int shipId)
        {
            var slot = _slots.FirstOrDefault(s => s.ShipEntityId == shipId);
            if (slot != null)
            {
                _slots.Remove(slot);
                Logger.Log($"[Formation {OwnerClientId}] Ship {shipId} removed from formation.");
                OnFormationLayoutChanged?.Invoke();
                return true;
            }
            Logger.LogWarning($"[Formation {OwnerClientId}] Attempted to remove ship {shipId} not found in formation.");
            return false;
        }

        public void RemoveAllShips()
        {
            if (_slots.Any())
            {
                _slots.Clear();
                Logger.Log($"[Formation {OwnerClientId}] All ships removed from formation.");
                OnFormationLayoutChanged?.Invoke();
            }
        }

        public Vector2 GetShipRelativeOffset(int shipId)
        {
            var slot = _slots.FirstOrDefault(s => s.ShipEntityId == shipId);
            // If slot is null (ship not in formation), returning Vector2.Zero might be misleading.
            // Consider throwing an exception or returning nullable Vector2? for robustness.
            // For now, assume ship will always be found if this is called for an active ship in escadre.
            if (slot == null)
            {
                Logger.LogWarning($"[Formation {OwnerClientId}] Ship {shipId} not found when trying to get relative offset. Returning Zero.");
                return Vector2.Zero;
            }
            return slot.RelativeOffset;
        }

        public Vector3 GetTargetWorldPositionForShip(int shipId, Vector3 escadreCenterPosition, Quaternion escadreOrientation)
        {
            Vector2 relativeOffset = GetShipRelativeOffset(shipId);
            Vector3 worldOffset = escadreOrientation * new Vector3(relativeOffset.X, 0, relativeOffset.Y);
            return escadreCenterPosition + worldOffset;
        }

        public bool IsCompleteFormationValid(out string reason)
        {
            return ValidationStrategy.IsValidFormation(_slots, out reason);
        }
    }
}