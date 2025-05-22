// File: Core/Model/Escadre.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    public class Escadre : Entity // Inherit from Entity
    {
        public override EntityTypeEnum EntityType => EntityTypeEnum.Escadre;

        public int OwnerClientId { get; }
        public string Nickname { get; private set; }

        // _level is inherited from Entity as protected
        // Level _level => base._level; // No, just use _level directly from base

        private readonly List<int> _shipEntityIds = new List<int>();
        public IReadOnlyList<int> ShipEntityIds => _shipEntityIds.AsReadOnly();

        private Vector2? _currentDestination;
        public Vector2? CurrentDestination { get => _currentDestination; private set => _currentDestination = value; }

        // _currentFormationAnchorTarget is now this.Position (from Entity)
        // _isFormationAnchorMoving: logic will determine if this.Position needs to change

        private readonly HashSet<int> _targetEscadreOwnerClientIds = new HashSet<int>(); // This might change to target Escadre Entity IDs
        public IReadOnlyCollection<int> TargetEscadreOwnerClientIds => _targetEscadreOwnerClientIds;
        
        // Store target Escadre Entity IDs
        private readonly HashSet<int> _targetEscadreEntityIds = new HashSet<int>();
        public IReadOnlyCollection<int> TargetEscadreEntityIds => _targetEscadreEntityIds;


        private int _resources;
        public int Resources { get => _resources; private set => _resources = value; }
        
        public event Action<int> OnResourcesChanged; 
        public event Action<Formation> OnFormationChanged; 

        public Formation CurrentFormation { get; private set; }
        private bool _isDisbanding = false; 

        private const float ESCADRE_MAX_SPEED = 3.5f;
        private const float ESCADRE_ACCELERATION = 2.0f;
        private float _currentEscadreSpeed = 0f;


        public Escadre(Level level, int ownerClientId, string nickname, Vector3 initialPosition) : base(level)
        {
            OwnerClientId = ownerClientId;
            Nickname = nickname ?? $"Escadre_{OwnerClientId}";
            // base._level is already set by Entity constructor
            
            this.Position = initialPosition; // Set initial position of the Escadre entity
            this.Rotation = Quaternion.Identity; // Default rotation

            _resources = 1000; 
            CurrentFormation = new Formation(OwnerClientId, new DefaultFormationValidationStrategy());
            CurrentFormation.OnFormationLayoutChanged += HandleInternalFormationLayoutChange;
            // _currentFormationAnchorTarget = CalculateCenterPoint(); // No, use this.Position
            // _isFormationAnchorMoving = false; // Will be determined by commands
        }

        private void HandleInternalFormationLayoutChange()
        {
            OnFormationChanged?.Invoke(CurrentFormation);
            if (!_isDisbanding) UpdateShipMovementTargets(_level.CurrentTime);
        }

        public void AddResources(int amount)
        {
            if (amount <= 0) return;
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
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Attempted to add invalid ship. Ship ID: {ship?.Id}");
                return;
            }
            if (!_shipEntityIds.Contains(ship.Id)) {
                _shipEntityIds.Add(ship.Id);
                // Formation logic remains similar
                bool addedToFormation;
                Vector2 finalOffset;
                if (initialFormationOffset.HasValue)
                {
                    addedToFormation = CurrentFormation.TryAddShip(ship.Id, initialFormationOffset.Value, out string reason);
                    finalOffset = initialFormationOffset.Value;
                    if(!addedToFormation) Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Failed to add ship {ship.Id} to formation at preferred offset {initialFormationOffset.Value}: {reason}");
                }
                else
                {
                    addedToFormation = CurrentFormation.AssignShipToAutoSlot(ship.Id, out finalOffset);
                     if(!addedToFormation) Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Failed to auto-assign ship {ship.Id} to formation.");
                }
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Added Ship {ship.Id} at formation offset {finalOffset}. Total ships: {_shipEntityIds.Count}");
            }
        }

        private void RemoveShip(int shipId)
        {
            if (_isDisbanding) return;
            if (_shipEntityIds.Remove(shipId)) {
                CurrentFormation.RemoveShip(shipId);
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Removed Ship {shipId}. Total ships: {_shipEntityIds.Count}");
                if (!_shipEntityIds.Any()) {
                    Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] All ships lost!");
                    _currentEscadreSpeed = 0f; // Stop moving if no ships
                }
            }
        }

        internal void HandleShipDestroyed(int shipId)
        {
            RemoveShip(shipId);
            // If all ships are destroyed, the Escadre itself should be considered destroyed.
            if (!_shipEntityIds.Any() && !IsDead) // Check !IsDead to prevent re-entry if already dying
            {
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] All ships lost! Initiating Escadre Kill().");
                this.Kill(); // This will trigger Death() and then Disband()
            }
        }

        public void SetCourse(Vector2 destination, float serverTime)
        {
            if (_isDisbanding || IsDead) return;
            CurrentDestination = destination;
            // _isFormationAnchorMoving = true; // Logic will determine if Position changes
            if (TargetEscadreEntityIds.Any() || TargetEscadreOwnerClientIds.Any()) { // Clear both target lists
                _targetEscadreEntityIds.Clear();
                _targetEscadreOwnerClientIds.Clear(); // Keep for compatibility if some systems still use it briefly
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Setting course to {destination}. Cancelling attack orders.");
            } else {
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Setting course to {destination}.");
            } 
            UpdateFormationAnchorAndShipTargets(0f, serverTime);
        }
        
        // New method to target Escadre Entity
        public void OrderAttackEscadreEntity(int targetEscadreEntityId, float serverTime)
        {
            if (_isDisbanding || IsDead) return;
            if (targetEscadreEntityId == this.Id) {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Cannot target self for attack.");
                return;
            }

            if (_level.TryGetEntity(targetEscadreEntityId, out Entity targetEntity) && targetEntity is Escadre targetEscadre)
            {
                if (_targetEscadreEntityIds.Add(targetEscadreEntityId)) {
                     Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Added attack order on escadre entity {targetEscadreEntityId} (Owner {targetEscadre.OwnerClientId}).");
                }
                // Optionally, for compatibility or if some systems still rely on it, populate TargetEscadreOwnerClientIds
                _targetEscadreOwnerClientIds.Add(targetEscadre.OwnerClientId);


                CurrentDestination = null; // Clear fixed destination
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Attack order on entity {targetEscadreEntityId} initiated. Cancelling fixed movement orders.");
                UpdateFormationAnchorAndShipTargets(0f, serverTime);
            }
            else
            {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] OrderAttackEscadreEntity: Target Escadre Entity ID {targetEscadreEntityId} not found or not an Escadre.");
            }
        }


        // Kept for potential compatibility, but should be deprecated in favor of OrderAttackEscadreEntity
        public void OrderAttack(int targetOwnerClientId, float serverTime)
        {
            if (_isDisbanding || IsDead) return;
            if (targetOwnerClientId == OwnerClientId) {
                Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Cannot target self (owner) for attack.");
                return;
            }
            // Find the Escadre entity for this owner
            Escadre targetEscadre = _level.GetAllEntities().OfType<Escadre>().FirstOrDefault(e => e.OwnerClientId == targetOwnerClientId && e.Id != this.Id);
            if (targetEscadre != null)
            {
                OrderAttackEscadreEntity(targetEscadre.Id, serverTime);
            }
            else
            {
                 Logger.LogWarning($"[Escadre {Id} (Owner {OwnerClientId})] Could not find an Escadre entity for owner {targetOwnerClientId} to attack.");
            }
        }


        public void OrderCancelAttack() 
        {
            if (_isDisbanding || IsDead) return;
            bool hadTargets = TargetEscadreEntityIds.Any() || TargetEscadreOwnerClientIds.Any();
            if (hadTargets) {
                _targetEscadreEntityIds.Clear();
                _targetEscadreOwnerClientIds.Clear();
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Cancelling ALL attack orders.");
                if (!CurrentDestination.HasValue) // If no other move order, escadre stops
                {
                    _currentEscadreSpeed = 0f;
                }
                UpdateFormationAnchorAndShipTargets(0f, _level.CurrentTime); 
            } else {
                Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] No active attack orders to cancel.");
            }
        }

        // Cancel attack on a specific escadre entity
        public void OrderCancelAttackOnEscadreEntity(int targetEscadreEntityId)
        {
            if (_isDisbanding || IsDead) return;
             if (_level.TryGetEntity(targetEscadreEntityId, out Entity targetEntity) && targetEntity is Escadre targetEscadre)
             {
                bool removed = _targetEscadreEntityIds.Remove(targetEscadreEntityId);
                removed |= _targetEscadreOwnerClientIds.Remove(targetEscadre.OwnerClientId); // Also remove by owner ID

                if (removed) {
                    Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Cancelled attack order on escadre entity {targetEscadreEntityId} (Owner {targetEscadre.OwnerClientId}).");
                    if (!TargetEscadreEntityIds.Any() && !CurrentDestination.HasValue) {
                         Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] No remaining attack targets or destinations. Escadre stopping.");
                         _currentEscadreSpeed = 0f;
                    }
                    UpdateFormationAnchorAndShipTargets(0f, _level.CurrentTime);
                }
             }
        }
        
        public bool RequestSetFormation(List<Tuple<int, Vector2>> newFormationSlots, float serverTime)
        {
            if (_isDisbanding || IsDead) return false;
            // ... (validation logic remains the same)
            var proposedSlots = new List<FormationSlot>();
            bool allShipsFound = true;
            foreach (var slotData in newFormationSlots)
            {
                if (!_shipEntityIds.Contains(slotData.Item1))
                {
                    Logger.LogWarning($"[Escadre {Id}] RequestSetFormation: Ship ID {slotData.Item1} not in escadre.");
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
                    Logger.LogWarning($"[Escadre {Id}] RequestSetFormation: Existing ship ID {existingShipId} is missing. Request rejected.");
                    return false; 
                }
            }

            if (!CurrentFormation.ValidationStrategy.IsValidFormation(proposedSlots, out string reason))
            {
                Logger.LogWarning($"[Escadre {Id}] Proposed formation is invalid: {reason}");
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
                Logger.Log($"[Escadre {Id}] Formation updated successfully via request.");
                return true;
            }
            return false; 
        }

        // Update is inherited from Entity. We override it to add Escadre-specific logic.
        public override void Update(float deltaTime)
        {
            base.Update(deltaTime); // Call base Entity.Update if it had any logic

            if (IsDead || _isDisbanding || !_shipEntityIds.Any())
            {
                _currentEscadreSpeed = 0f;
                return;
            }
            
            // If there's a destination or attack target, update anchor and ship targets
            if (CurrentDestination.HasValue || TargetEscadreEntityIds.Any())
            {
                UpdateFormationAnchorAndShipTargets(deltaTime, _level.CurrentTime);
            } else { // No explicit move/attack order
                 _currentEscadreSpeed = Math.Max(0, _currentEscadreSpeed - ESCADRE_ACCELERATION * deltaTime * 2f); // Decelerate if no order
                 if (_currentEscadreSpeed == 0) {
                    // If stopped and no orders, ensure ships just hold formation relative to static anchor
                    // UpdateShipMovementTargets(_level.CurrentTime); // could be called to reinforce, but UpdateFormationAnchorAndShipTargets handles it
                 }
            }
            // Individual ships still run their own Ship.Update() via Level.DoUpdate()
        }

        private void UpdateFormationAnchorAndShipTargets(float deltaTime, float serverTime)
        {
            if (IsDead) return;

            Vector3 ultimateTargetPoint = this.Position; // Default to current anchor if no other goal
            bool hasUltimateTarget = false;
            bool shouldBeMoving = false;


            if (CurrentDestination.HasValue)
            {
                ultimateTargetPoint = new Vector3(CurrentDestination.Value.X, this.Position.Y, CurrentDestination.Value.Y);
                hasUltimateTarget = true;
                shouldBeMoving = true;
            }
            else if (TargetEscadreEntityIds.Any())
            {
                int primaryTargetEntityId = TargetEscadreEntityIds.First();
                if (_level.TryGetEntity(primaryTargetEntityId, out Entity enemyEntity) && enemyEntity is Escadre enemyEscadre && !enemyEscadre.IsDead)
                {
                    if (enemyEscadre.ShipEntityIds.Any()) // Only move if target has ships
                    {
                        ultimateTargetPoint = enemyEscadre.Position; // Target the enemy escadre's anchor
                        hasUltimateTarget = true;
                        shouldBeMoving = true;
                    }
                    else // Target escadre has no ships, remove it as a target
                    {
                        Logger.Log($"[Escadre {Id}] Target Escadre {primaryTargetEntityId} has no ships. Removing as attack target.");
                        _targetEscadreEntityIds.Remove(primaryTargetEntityId);
                        _targetEscadreOwnerClientIds.Remove(enemyEscadre.OwnerClientId); // Also from old list
                        if (!TargetEscadreEntityIds.Any()) shouldBeMoving = false; // Stop if no more targets
                    }
                }
                else // Target escadre entity doesn't exist or is dead
                {
                    Logger.Log($"[Escadre {Id}] Target Escadre {primaryTargetEntityId} not found/valid/alive. Removing as attack target.");
                    _targetEscadreEntityIds.Remove(primaryTargetEntityId);
                    // Attempt to remove from owner ID list too, if possible (requires lookup or storing mapping)
                    // For now, just clear from entity ID list.
                    if (!TargetEscadreEntityIds.Any()) shouldBeMoving = false;
                }
            }
            else // No destination, no attack targets
            {
                 shouldBeMoving = false;
            }

            if (!shouldBeMoving)
            {
                _currentEscadreSpeed = Math.Max(0, _currentEscadreSpeed - ESCADRE_ACCELERATION * deltaTime * 2f); // Decelerate
            }
            else if (hasUltimateTarget && deltaTime >= 0) // Allow 0 deltaTime for initial setup
            {
                Vector3 directionToUltimateTarget = ultimateTargetPoint - this.Position;
                float distanceToUltimateTargetSq = directionToUltimateTarget.SqrMagnitude;

                float stoppingDistThreshold = 0.5f; // Stop if this close
                if (distanceToUltimateTargetSq < stoppingDistThreshold * stoppingDistThreshold)
                {
                    this.Position = ultimateTargetPoint; // Snap to final point
                    _currentEscadreSpeed = 0f;
                    if (CurrentDestination.HasValue) CurrentDestination = null; // Clear fixed destination once reached
                    // If it was an attack target, don't clear it - stay near it.
                }
                else
                {
                    if (_currentEscadreSpeed < ESCADRE_MAX_SPEED)
                    {
                        _currentEscadreSpeed = Math.Min(ESCADRE_MAX_SPEED, _currentEscadreSpeed + ESCADRE_ACCELERATION * deltaTime);
                    }
                    // Update Escadre's own position
                    this.Position += directionToUltimateTarget.Normalized * _currentEscadreSpeed * deltaTime;
                    
                    // Update Escadre's rotation to face movement direction
                    if (_currentEscadreSpeed > 0.1f && directionToUltimateTarget.SqrMagnitude > Vector3.Epsilon)
                    {
                        this.Rotation = Quaternion.LookRotation(directionToUltimateTarget.Normalized, Vector3.Up);
                    }
                }
            }
            UpdateShipMovementTargets(serverTime);
        }

        // CalculateEscadreOrientationForMovement is now handled by this.Rotation which is updated in UpdateFormationAnchorAndShipTargets
        // internal Quaternion CalculateEscadreOrientationForMovement() { ... } // REMOVE

        internal void UpdateShipMovementTargets(float serverTime)
        {
            if (_isDisbanding || IsDead || !_shipEntityIds.Any()) return;

            Quaternion escadreOrientation = this.Rotation; // Use the Escadre entity's current rotation

            foreach (int shipId in _shipEntityIds)
            {
                if (_level.TryGetEntity(shipId, out Entity entity) && entity is Ship ship && !ship.IsDead)
                {
                    // Use this.Position as the escadreCenterPosition
                    Vector3 targetShipWorldPosition = CurrentFormation.GetTargetWorldPositionForShip(shipId, this.Position, escadreOrientation);
                    ship.SetMovementTarget(new Vector2(targetShipWorldPosition.X, targetShipWorldPosition.Z), serverTime);
                }
            }
        }

        // CalculateCenterPoint is used by ClientConnection.Position, still useful for PVS center if Escadre entity Position isn't it.
        // However, Escadre.Position *should* be the PVS center.
        // This method can be used to determine the *actual geometric center* of ships if needed,
        // distinct from the formation anchor (this.Position).
        public Vector3 CalculateGeometricCenterOfShips()
        {
            if (!_shipEntityIds.Any()) return this.Position; // Fallback to anchor if no ships
            Vector3 sumPositions = Vector3.Zero; int aliveShipCount = 0;
            foreach (int shipIdInList in _shipEntityIds) {
                if (_level.TryGetEntity(shipIdInList, out Entity entityInLevel) && entityInLevel is Ship && !entityInLevel.IsDead) {
                    sumPositions += entityInLevel.Position; aliveShipCount++;
                }
            }
            return aliveShipCount > 0 ? sumPositions / aliveShipCount : this.Position; 
        }

        internal void Disband(bool silentKillShips = true)
        {
            _isDisbanding = true; 
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Disbanding (silentKillShips: {silentKillShips}).");
            
            // Clear attack/move orders
            CurrentDestination = null;
            _targetEscadreEntityIds.Clear();
            _targetEscadreOwnerClientIds.Clear();
            _currentEscadreSpeed = 0f;

            var idsToKill = new List<int>(_shipEntityIds); 
            foreach (int shipIdInList in idsToKill) {
                if (_level.TryGetEntity(shipIdInList, out Entity entityInLevel) && !entityInLevel.IsDead)
                {
                    entityInLevel.Kill(silentKillShips); 
                }
            }
            _shipEntityIds.Clear(); 
            CurrentFormation.RemoveAllShips(); 
            _isDisbanding = false; // Reset flag after disbanding
        }

        // Override Entity.Death() for specific Escadre death behavior
        protected override void Death()
        {
            base.Death(); // Call base Entity.Death if it has any logic
            Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Performing Death(). Disbanding ships non-silently.");
            Disband(false); // Disband ships, they can have their own destruction effects
        }

        protected override void ObligatoryOnRemove()
        {
            base.ObligatoryOnRemove();
             Logger.Log($"[Escadre {Id} (Owner {OwnerClientId})] Performing ObligatoryOnRemove().");
            // Ensure all resources are cleaned up
            if (!_isDisbanding) // If not already called by Kill->Death->Disband
            {
                Disband(true); // Silently clean up ships if escadre is removed directly
            }
            // Nullify events to help GC and prevent further calls
            OnResourcesChanged = null;
            OnFormationChanged = null;
            if (CurrentFormation != null)
            {
                CurrentFormation.OnFormationLayoutChanged -= HandleInternalFormationLayoutChange;
                CurrentFormation.RemoveAllShips(); // Ensure formation is also cleared
            }
        }
    }
}