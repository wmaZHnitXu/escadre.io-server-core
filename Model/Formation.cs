// File: Core/Model/Formation.cs
using Core.Primitives;
using Core.Logging;
using System.Collections.Generic;
using System.Linq;
using System; 

namespace Core.Model
{
    public class Formation
    {
        public int OwnerClientId { get; } 
        private readonly List<FormationSlot> _slots = new List<FormationSlot>();
        public IReadOnlyList<FormationSlot> Slots => _slots.AsReadOnly();

        public IFormationValidationStrategy ValidationStrategy { get; set; }

        public event Action OnFormationLayoutChanged; 

        public Formation(int ownerClientId, IFormationValidationStrategy initialStrategy)
        {
            OwnerClientId = ownerClientId;
            ValidationStrategy = initialStrategy ?? throw new ArgumentNullException(nameof(initialStrategy));
        }

        private void RecalculateAndCenterOffsets()
        {
            if (!_slots.Any()) return;

            Vector2 averageOffset = Vector2.Zero;
            foreach (var slot in _slots)
            {
                averageOffset += slot.RelativeOffset;
            }
            averageOffset /= _slots.Count;

            // If averageOffset is already effectively zero, no need to modify slots
            // This check is important for precision and to avoid tiny drifts.
            if (averageOffset.SqrMagnitude < Vector2.Epsilon * Vector2.Epsilon)
            {
                // Check if it's a single ship already at true zero (important for the "one ship at (0,0)" rule)
                if (_slots.Count == 1 && _slots[0].RelativeOffset.SqrMagnitude < Vector2.Epsilon * Vector2.Epsilon) {
                    return; 
                }
                // If multiple ships and average is zero, they are already centered.
                if (_slots.Count > 1) {
                    return;
                }
            }
            
            for (int i = 0; i < _slots.Count; i++)
            {
                _slots[i].RelativeOffset -= averageOffset;
            }
            // Logger.Log($"[Formation {OwnerClientId}] Recalculated and centered {_slots.Count} slot offsets. Average offset was {averageOffset}.");
        }


        public bool TrySetShipRelativeOffset(int shipId, Vector2 newRawRelativeOffset, out string reason)
        {
            var slot = _slots.FirstOrDefault(s => s.ShipEntityId == shipId);
            if (slot == null)
            {
                reason = $"Ship ID {shipId} not found in formation.";
                return false;
            }

            var otherSlots = _slots.Where(s => s.ShipEntityId != shipId).ToList();

            // Validate the newRawRelativeOffset against the existing slots (which are already centered)
            // The validation strategy should interpret newRawRelativeOffset as if it's being added to the current centered group.
            if (!ValidationStrategy.IsValidPosition(otherSlots, newRawRelativeOffset, shipId, out reason))
            {
                return false;
            }

            slot.RelativeOffset = newRawRelativeOffset; // Set the raw offset
            RecalculateAndCenterOffsets(); // Re-center all slots including the updated one
            Logger.Log($"[Formation {OwnerClientId}] Ship {shipId} raw offset changed to {newRawRelativeOffset}. Formation re-centered.");
            OnFormationLayoutChanged?.Invoke();
            return true;
        }

        public bool TryAddShip(int shipId, Vector2 preferredRawOffset, out string reason)
        {
            if (_slots.Any(s => s.ShipEntityId == shipId))
            {
                reason = $"Ship ID {shipId} already in formation.";
                return false;
            }
            
            // Validate preferredRawOffset against existing centered slots
            // The strategy is validating the `preferredRawOffset` based on the current state of `_slots`.
            if (!ValidationStrategy.IsValidPosition(_slots, preferredRawOffset, shipId, out reason))
            {
                if (ValidationStrategy.TrySuggestInitialOffset(_slots, out Vector2 suggestedRawOffset))
                {
                    if (!ValidationStrategy.IsValidPosition(_slots, suggestedRawOffset, shipId, out string suggestedReason))
                    {
                        reason = $"Preferred raw offset {preferredRawOffset} invalid ({reason}). Suggested raw offset {suggestedRawOffset} also invalid ({suggestedReason}).";
                        return false;
                    }
                    preferredRawOffset = suggestedRawOffset; 
                    Logger.Log($"[Formation {OwnerClientId}] Preferred raw offset for ship {shipId} was invalid. Using suggested raw offset {preferredRawOffset}. Original reason: {reason}");
                    reason = $"Used suggested raw offset {preferredRawOffset}."; 
                }
                else
                {
                    reason = $"Preferred raw offset {preferredRawOffset} invalid ({reason}). Could not suggest an alternative.";
                    return false;
                }
            }

            _slots.Add(new FormationSlot(preferredRawOffset, shipId)); // Add with raw offset
            RecalculateAndCenterOffsets(); // Then re-center the whole formation
            Logger.Log($"[Formation {OwnerClientId}] Ship {shipId} added to formation with raw offset {preferredRawOffset}. Formation re-centered.");
            OnFormationLayoutChanged?.Invoke();
            return true;
        }
        
        public bool AssignShipToAutoSlot(int shipId, out Vector2 assignedRawOffset)
        {
            assignedRawOffset = Vector2.Zero;
            if (_slots.Any(s => s.ShipEntityId == shipId))
            {
                // This case should ideally be caught before calling (e.g., in Escadre.AddShip)
                Logger.LogWarning($"[Formation {OwnerClientId}] Ship ID {shipId} already in formation. Auto-assign failed.");
                return false; 
            }

            if (ValidationStrategy.TrySuggestInitialOffset(_slots, out Vector2 suggestedRawOffset))
            {
                // Validate the suggestedRawOffset against the current (centered) _slots.
                if (ValidationStrategy.IsValidPosition(_slots, suggestedRawOffset, shipId, out _)) 
                {
                    _slots.Add(new FormationSlot(suggestedRawOffset, shipId)); // Add with raw suggested offset
                    RecalculateAndCenterOffsets(); // Then re-center
                    Logger.Log($"[Formation {OwnerClientId}] Ship {shipId} auto-assigned to formation with raw offset {suggestedRawOffset}. Formation re-centered.");
                    assignedRawOffset = suggestedRawOffset; // Return the raw offset that was used for addition
                    OnFormationLayoutChanged?.Invoke();
                    return true;
                }
                 Logger.LogWarning($"[Formation {OwnerClientId}] Suggested raw offset {suggestedRawOffset} for ship {shipId} was invalid after validation.");
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
                RecalculateAndCenterOffsets(); // Re-center after removal
                Logger.Log($"[Formation {OwnerClientId}] Ship {shipId} removed from formation. Formation re-centered.");
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
                // No need to RecalculateAndCenterOffsets as list is empty.
                Logger.Log($"[Formation {OwnerClientId}] All ships removed from formation.");
                OnFormationLayoutChanged?.Invoke();
            }
        }

        /// <summary>
        /// Gets the ship's relative offset, which is always centered by this formation manager.
        /// </summary>
        public Vector2 GetShipRelativeOffset(int shipId)
        {
            var slot = _slots.FirstOrDefault(s => s.ShipEntityId == shipId);
            if (slot == null)
            {
                Logger.LogWarning($"[Formation {OwnerClientId}] Ship {shipId} not found when trying to get relative offset. Returning Zero.");
                return Vector2.Zero; 
            }
            // The _slots[i].RelativeOffset is already centered due to RecalculateAndCenterOffsets() calls.
            return slot.RelativeOffset;
        }

        /// <summary>
        /// Gets the target world position for a ship in the formation, applying scaling to the centered offset.
        /// This method is now primarily for conceptual understanding or external queries, as Escadre.UpdateShipMovementTargets
        /// performs the actual calculation for ship commands.
        /// </summary>
        public Vector3 GetTargetWorldPositionForShip(int shipId, Vector3 escadreWorldPosition, Quaternion escadreOrientation, float formationScale)
        {
            Vector2 centeredRelativeOffset = GetShipRelativeOffset(shipId);
            Vector2 scaledOffset = centeredRelativeOffset * formationScale;
            Vector3 worldOffset = escadreOrientation * new Vector3(scaledOffset.X, 0, scaledOffset.Y);
            return escadreWorldPosition + worldOffset;
        }

        public bool IsCompleteFormationValid(out string reason)
        {
            // Validation strategy operates on the currently stored (centered) slots.
            return ValidationStrategy.IsValidFormation(_slots, out reason);
        }
    }
}