// File: Core/Model/Escadre.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    public class Escadre
    {
        public int OwnerClientId { get; }
        private readonly Level _level;

        private readonly List<int> _shipEntityIds = new List<int>();
        public IReadOnlyList<int> ShipEntityIds => _shipEntityIds.AsReadOnly();

        private Vector2? _currentDestination; // The ultimate destination for the escadre
        public Vector2? CurrentDestination { get => _currentDestination; private set => _currentDestination = value; }

        // This will represent the current target position for the formation's center.
        // It moves towards _currentDestination or an attack target.
        private Vector3 _currentFormationAnchorTarget;
        private bool _isFormationAnchorMoving;


        private readonly HashSet<int> _targetEscadreOwnerClientIds = new HashSet<int>();
        public IReadOnlyCollection<int> TargetEscadreOwnerClientIds => _targetEscadreOwnerClientIds;

        private int _resources;
        public int Resources { get => _resources; private set => _resources = value; }
        
        public event Action<int> OnResourcesChanged; 
        public event Action<Formation> OnFormationChanged; 

        public Formation CurrentFormation { get; private set; }
        private bool _isDisbanding = false; 

        // Escadre-level movement parameters (can be adjusted)
        private const float ESCADRE_MAX_SPEED = 3.5f; // Slightly slower than a default ship for formation cohesion
        private const float ESCADRE_ACCELERATION = 2.0f;
        private float _currentEscadreSpeed = 0f;


        public Escadre(int ownerClientId, Level level)
        {
            OwnerClientId = ownerClientId;
            _level = level ?? throw new ArgumentNullException(nameof(level));
            _resources = 1000; 
            CurrentFormation = new Formation(OwnerClientId, new DefaultFormationValidationStrategy());
            CurrentFormation.OnFormationLayoutChanged += HandleInternalFormationLayoutChange;
            _currentFormationAnchorTarget = CalculateCenterPoint(); // Initialize to current position
            _isFormationAnchorMoving = false;
        }

        private void HandleInternalFormationLayoutChange()
        {
            OnFormationChanged?.Invoke(CurrentFormation);
            // Re-evaluate ship targets if formation changes structurally, even if escadre isn't moving.
            // This ensures ships move to their new slots.
            if (!_isDisbanding) UpdateShipMovementTargets(_level.CurrentTime);
        }

        public void AddResources(int amount)
        {
            if (amount <= 0) {
                return;
            }
            _resources += amount;
            OnResourcesChanged?.Invoke(_resources);
        }

        public bool DeductResources(int amount)
        {
            if (amount <= 0) return true; 
            if (_resources >= amount)
            {
                _resources -= amount;
                OnResourcesChanged?.Invoke(_resources);
                return true;
            }
            return false;
        }

        public int GetUpgradeCostForShip(Ship ship)
        {
            return 50;
        }

        internal void AddShip(Ship ship, Vector2? initialFormationOffset = null)
        {
            if (_isDisbanding) return;
            if (ship == null || ship.OwningEscadreClientId != OwnerClientId) {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Attempted to add invalid ship (null or wrong owner). Ship ID: {ship?.Id}");
                return;
            }
            if (!_shipEntityIds.Contains(ship.Id)) {
                _shipEntityIds.Add(ship.Id);

                bool addedToFormation;
                Vector2 finalOffset;
                if (initialFormationOffset.HasValue)
                {
                    addedToFormation = CurrentFormation.TryAddShip(ship.Id, initialFormationOffset.Value, out string reason);
                    finalOffset = initialFormationOffset.Value; // Use the preferred/validated offset
                    if(!addedToFormation) Logger.LogWarning($"[Escadre {OwnerClientId}] Failed to add ship {ship.Id} to formation at preferred offset {initialFormationOffset.Value}: {reason}");
                }
                else
                {
                    addedToFormation = CurrentFormation.AssignShipToAutoSlot(ship.Id, out finalOffset);
                     if(!addedToFormation) Logger.LogWarning($"[Escadre {OwnerClientId}] Failed to auto-assign ship {ship.Id} to formation.");
                }
                
                Logger.Log($"[Escadre {OwnerClientId}] Added Ship {ship.Id} at formation offset {finalOffset}. Total ships: {_shipEntityIds.Count}");
                // OnFormationChanged is triggered by the Formation itself.
                // UpdateShipMovementTargets will be called via HandleInternalFormationLayoutChange
            }
        }

        private void RemoveShip(int shipId)
        {
            if (_isDisbanding) return;
            if (_shipEntityIds.Remove(shipId)) {
                CurrentFormation.RemoveShip(shipId);
                Logger.Log($"[Escadre {OwnerClientId}] Removed Ship {shipId}. Total ships: {_shipEntityIds.Count}");
                if (!_shipEntityIds.Any()) {
                    Logger.Log($"[Escadre {OwnerClientId}] All ships lost!");
                    _isFormationAnchorMoving = false; // Stop moving if no ships
                    _currentEscadreSpeed = 0f;
                }
                // UpdateShipMovementTargets is called by HandleInternalFormationLayoutChange
            }
        }

        internal void HandleShipDestroyed(int shipId)
        {
            RemoveShip(shipId);
            // If not disbanding, formation layout change will trigger UpdateShipMovementTargets.
        }

        public void SetCourse(Vector2 destination, float serverTime)
        {
            if (_isDisbanding) return;
            CurrentDestination = destination;
            _isFormationAnchorMoving = true; // Start/continue moving the anchor
            if (TargetEscadreOwnerClientIds.Any()) {
                _targetEscadreOwnerClientIds.Clear();
                Logger.Log($"[Escadre {OwnerClientId}] Setting course to {destination}. Cancelling attack orders.");
            } else {
                Logger.Log($"[Escadre {OwnerClientId}] Setting course to {destination}.");
            } 
            // UpdateShipMovementTargets will be called by the main Escadre.Update() loop now
            // based on the moving _currentFormationAnchorTarget.
            // We can give an initial kick here if needed, or let the Update loop handle it.
            // For immediate response:
            UpdateFormationAnchorAndShipTargets(0f, serverTime); // Pass 0 delta, use serverTime
        }

        public void OrderAttack(int targetOwnerClientId, float serverTime)
        {
            if (_isDisbanding) return;
            if (targetOwnerClientId == OwnerClientId) {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Cannot target self for attack.");
                return;
            }

            if (_targetEscadreOwnerClientIds.Add(targetOwnerClientId)) {
                Logger.Log($"[Escadre {OwnerClientId}] Added attack order on escadre of Client {targetOwnerClientId}.");
            }

            CurrentDestination = null; // Clear fixed destination
            _isFormationAnchorMoving = true; // Escadre should move towards attack target
            Logger.Log($"[Escadre {OwnerClientId}] Attack order initiated. Cancelling fixed movement orders.");
            UpdateFormationAnchorAndShipTargets(0f, serverTime);
        }

        public void OrderCancelAttack() 
        {
            if (_isDisbanding) return;
            if (TargetEscadreOwnerClientIds.Any()) {
                _targetEscadreOwnerClientIds.Clear();
                Logger.Log($"[Escadre {OwnerClientId}] Cancelling ALL attack orders.");
                if (!CurrentDestination.HasValue) // If no other move order, escadre stops
                {
                    _isFormationAnchorMoving = false;
                    _currentEscadreSpeed = 0f;
                }
                UpdateFormationAnchorAndShipTargets(0f, _level.CurrentTime); 
            } else {
                Logger.Log($"[Escadre {OwnerClientId}] No active attack orders to cancel.");
            }
        }

        public void OrderCancelAttackOn(int targetOwnerClientId)
        {
            if (_isDisbanding) return;
            if (_targetEscadreOwnerClientIds.Remove(targetOwnerClientId)) {
                Logger.Log($"[Escadre {OwnerClientId}] Cancelled attack order on client {targetOwnerClientId}.");
                if (!TargetEscadreOwnerClientIds.Any() && !CurrentDestination.HasValue) {
                     Logger.Log($"[Escadre {OwnerClientId}] No remaining attack targets or destinations. Escadre stopping.");
                     _isFormationAnchorMoving = false;
                     _currentEscadreSpeed = 0f;
                }
                UpdateFormationAnchorAndShipTargets(0f, _level.CurrentTime);
            }
        }
        
        public bool RequestSetFormation(List<Tuple<int, Vector2>> newFormationSlots, float serverTime)
        {
            if (_isDisbanding) return false;

            var proposedSlots = new List<FormationSlot>();
            bool allShipsFound = true;
            foreach (var slotData in newFormationSlots)
            {
                if (!_shipEntityIds.Contains(slotData.Item1))
                {
                    Logger.LogWarning($"[Escadre {OwnerClientId}] RequestSetFormation: Ship ID {slotData.Item1} not in escadre.");
                    allShipsFound = false;
                    break;
                }
                proposedSlots.Add(new FormationSlot(slotData.Item2, slotData.Item1));
            }

            if (!allShipsFound) return false; 

            foreach(var existingShipId in _shipEntityIds)
            {
                if (!newFormationSlots.Any(s => s.Item1 == existingShipId))
                {
                    Logger.LogWarning($"[Escadre {OwnerClientId}] RequestSetFormation: Existing ship ID {existingShipId} is missing. Request rejected.");
                    return false; 
                }
            }

            if (!CurrentFormation.ValidationStrategy.IsValidFormation(proposedSlots, out string reason))
            {
                Logger.LogWarning($"[Escadre {OwnerClientId}] Proposed formation is invalid: {reason}");
                return false;
            }

            bool changed = false;
            foreach (var slotData in newFormationSlots)
            {
                if (CurrentFormation.TrySetShipRelativeOffset(slotData.Item1, slotData.Item2, out _))
                {
                    changed = true; 
                }
            }
            
            if (changed)
            {
                Logger.Log($"[Escadre {OwnerClientId}] Formation updated successfully via request.");
                // OnFormationChanged event is fired by Formation.TrySetShipRelativeOffset.
                // This will trigger UpdateShipMovementTargets via HandleInternalFormationLayoutChange
                return true;
            }
            return false; 
        }

        // This method will be called by Level or CoreComposer in its update loop
        public void Update(float deltaTime, float serverTime)
        {
            if (_isDisbanding || !_shipEntityIds.Any())
            {
                _isFormationAnchorMoving = false;
                _currentEscadreSpeed = 0f;
                return;
            }

            if (_isFormationAnchorMoving)
            {
                UpdateFormationAnchorAndShipTargets(deltaTime, serverTime);
            }
            // Else: If not moving, ships just hold their formation spots relative to the static anchor.
            // Individual ships still run their own Ship.Update() for attack logic etc.
        }

        private void UpdateFormationAnchorAndShipTargets(float deltaTime, float serverTime)
        {
            Vector3 ultimateTargetPoint = _currentFormationAnchorTarget; // Default to current anchor if no other goal
            bool hasUltimateTarget = false;

            if (CurrentDestination.HasValue)
            {
                ultimateTargetPoint = new Vector3(CurrentDestination.Value.X, _currentFormationAnchorTarget.Y, CurrentDestination.Value.Y);
                hasUltimateTarget = true;
            }
            else if (TargetEscadreOwnerClientIds.Any())
            {
                // Determine attack focus point (e.g., center of primary target escadre)
                int primaryTargetClientId = TargetEscadreOwnerClientIds.First(); // Simplistic: first target
                if (_level.TryGetEscadre(primaryTargetClientId, out Escadre enemyEscadre))
                {
                    if (enemyEscadre.ShipEntityIds.Any())
                    {
                        ultimateTargetPoint = enemyEscadre.CalculateCenterPoint();
                        hasUltimateTarget = true;
                    }
                    else // Target has no ships
                    {
                        _targetEscadreOwnerClientIds.Remove(primaryTargetClientId); // Remove empty target
                        if (!TargetEscadreOwnerClientIds.Any()) _isFormationAnchorMoving = false; // No more targets
                    }
                }
                else // Target escadre doesn't exist
                {
                    _targetEscadreOwnerClientIds.Remove(primaryTargetClientId);
                     if (!TargetEscadreOwnerClientIds.Any()) _isFormationAnchorMoving = false;
                }
            }
            else // No destination, no attack targets
            {
                 _isFormationAnchorMoving = false;
            }

            if (!_isFormationAnchorMoving) // If any of the above conditions set it to false
            {
                _currentEscadreSpeed = 0f;
                // Ships should still maintain formation around the last known anchor.
                // UpdateShipMovementTargets(serverTime) will use the static _currentFormationAnchorTarget.
            }
            else if (hasUltimateTarget && deltaTime > 0) // Only move anchor if deltaTime > 0 (i.e., not an initial setup call)
            {
                Vector3 directionToUltimateTarget = ultimateTargetPoint - _currentFormationAnchorTarget;
                float distanceToUltimateTargetSq = directionToUltimateTarget.SqrMagnitude;

                float stoppingDist = ESCADRE_MAX_SPEED * deltaTime * 0.5f; // Escadre stopping threshold
                if (distanceToUltimateTargetSq < stoppingDist * stoppingDist)
                {
                    _currentFormationAnchorTarget = ultimateTargetPoint; // Snap to final point
                    _isFormationAnchorMoving = false; // Reached destination
                    _currentEscadreSpeed = 0f;
                    if (CurrentDestination.HasValue) CurrentDestination = null; // Clear fixed destination once reached
                }
                else
                {
                    if (_currentEscadreSpeed < ESCADRE_MAX_SPEED)
                    {
                        _currentEscadreSpeed = Math.Min(ESCADRE_MAX_SPEED, _currentEscadreSpeed + ESCADRE_ACCELERATION * deltaTime);
                    }
                    _currentFormationAnchorTarget += directionToUltimateTarget.Normalized * _currentEscadreSpeed * deltaTime;
                }
            }
            // After anchor update (or if static), update individual ship targets
            UpdateShipMovementTargets(serverTime);
        }


        internal Quaternion CalculateEscadreOrientationForMovement()
        {
            Vector3 referencePointForOrientation;
            bool useDynamicOrientation = false;

            if (CurrentDestination.HasValue)
            {
                referencePointForOrientation = new Vector3(CurrentDestination.Value.X, _currentFormationAnchorTarget.Y, CurrentDestination.Value.Y);
                useDynamicOrientation = true;
            }
            else if (TargetEscadreOwnerClientIds.Any())
            {
                int primaryTargetClientId = TargetEscadreOwnerClientIds.First();
                if (_level.TryGetEscadre(primaryTargetClientId, out Escadre enemyEscadre) && enemyEscadre.ShipEntityIds.Any())
                {
                    referencePointForOrientation = enemyEscadre.CalculateCenterPoint();
                    useDynamicOrientation = true;
                }
                else { referencePointForOrientation = _currentFormationAnchorTarget + Vector3.Forward; } // Default if target invalid
            }
            else // Idle or no specific target
            {
                 // Maintain last known orientation or average of ships. For simplicity, use a default forward if truly idle.
                 // If _currentEscadreSpeed is near zero, use ships' average. Otherwise, last commanded orientation.
                 // This requires storing last commanded orientation.
                 // For now, if anchor isn't moving, ships just keep their individual orientations towards their slots.
                 // Let's try to make it face the direction of _currentEscadreSpeed if any, else avg.
                if (_currentEscadreSpeed > 0.1f && _isFormationAnchorMoving) // If moving generally
                {
                    // This needs a target point for orientation which is currently 'ultimateTargetPoint'
                    // from UpdateFormationAnchorAndShipTargets. This calculation should be harmonized.
                    // For simplicity: if moving, point in direction of recent movement.
                    // This state is not directly available, so this part is tricky without more state.
                    // Fallback to current average or identity if truly idle.
                }

                // If not dynamically orienting, what should it be?
                // Average rotation of ships (complex) or just return identity / last set.
                // For now, let ships orient to their slots relative to a non-rotating anchor if escadre is idle.
                if (!_isFormationAnchorMoving && _shipEntityIds.Any())
                {
                    // Try to maintain an average orientation if idle - difficult to do robustly
                    // return Quaternion.Identity; // Simplest: formation doesn't "turn" when idle.
                    // Or, if we stored last commanded orientation:
                    // return _lastCommandedOrientation;

                    // A slightly better idle: face the average "forward" of the ships in formation, if they are aligned.
                    // This still can be jittery.
                }
                 referencePointForOrientation = _currentFormationAnchorTarget + Vector3.Forward; // Default non-dynamic orientation
            }

            if (useDynamicOrientation)
            {
                Vector3 direction = referencePointForOrientation - _currentFormationAnchorTarget;
                if (direction.SqrMagnitude > Vector3.Epsilon)
                {
                    return Quaternion.LookRotation(direction.Normalized, Vector3.Up);
                }
            }
            
            // Fallback or idle orientation
            // If ships are present, average their forward vectors (normalized) and derive orientation.
            // This is complex. Simpler: use identity or last set orientation.
            // If _lastCommandedOrientation was stored, return that.
            // For now, if no dynamic target, just identity. Ships will orient to their slots.
            return Quaternion.Identity;
        }


        internal void UpdateShipMovementTargets(float serverTime)
        {
            if (_isDisbanding || !_shipEntityIds.Any()) return;

            // Use the _currentFormationAnchorTarget which is now updated by Escadre.Update()
            Quaternion escadreOrientation = CalculateEscadreOrientationForMovement();

            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    Vector3 targetShipWorldPosition = CurrentFormation.GetTargetWorldPositionForShip(shipId, _currentFormationAnchorTarget, escadreOrientation);
                    ship.SetMovementTarget(new Vector2(targetShipWorldPosition.X, targetShipWorldPosition.Z), serverTime);
                }
            }
        }


        public Vector3 CalculateCenterPoint()
        {
            if (!_shipEntityIds.Any()) return Vector3.Zero; 
            Vector3 sumPositions = Vector3.Zero; int aliveShipCount = 0;
            foreach (int shipIdInList in _shipEntityIds) {
                if (_level.TryGetEntity(shipIdInList, out Entity entityInLevel) && !entityInLevel.IsDead) {
                    sumPositions += entityInLevel.Position; aliveShipCount++;
                }
            }
            return aliveShipCount > 0 ? sumPositions / aliveShipCount : Vector3.Zero; 
        }

        internal void Disband(bool silentKill = true)
        {
            _isDisbanding = true; 
            Logger.Log($"[Escadre {OwnerClientId}] Disbanding (silent: {silentKill}).");
            var idsToKill = new List<int>(_shipEntityIds); 
            foreach (int shipIdInList in idsToKill) {
                if (_level.TryGetEntity(shipIdInList, out Entity entityInLevel) && !entityInLevel.IsDead)
                {
                    entityInLevel.Kill(silentKill); 
                }
            }
            _shipEntityIds.Clear(); 
            CurrentFormation.RemoveAllShips(); 
            _isDisbanding = false; 
        }
    }
}