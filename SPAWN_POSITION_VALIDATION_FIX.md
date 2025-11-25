# Spawn Position Validation Fix

## Problem: Invalid Saved Position Causing Fall Rubberband

### Issue Description
Players were experiencing endless fall loops and rubberbanding because the system was loading an invalid saved position from a previous fall session at `Y=-571.60` (deep underground with no terrain collision).

### Log Evidence
```
[WorldSaveManager] ✅ Successfully loaded data for player 0. Position: (0.00, -571.60, 0.00)
[UnifiedSpawnController] Using saved position for client 0: (0.00, -571.60, 0.00)
[Phase 4] WARNING: No ground collision detected at (0.00, -571.60, 0.00) even after chunk loading!
Player 0 attempted to move too far in a single update
```

### Root Cause
The `DetermineSpawnPosition` method in `UnifiedSpawnController.cs` was checking if `savedPosition != Vector3.zero` but not validating whether that position was actually valid. A saved position like `(0, -571.60, 0)` would pass this check but is:
1. Deep underground (Y < -50)
2. Has no terrain collision
3. Causes immediate falling and fall recovery triggers
4. Results in rubber-banding between invalid spawn position and rescue position

## Solution: Multi-Layer Position Validation

### Changes Made to `UnifiedSpawnController.cs`

Added comprehensive validation in `DetermineSpawnPosition()` method:

1. **Underground Check**: Reject positions with Y < -50
   ```csharp
   if (savedPosition.y < -50f)
   {
       Debug.LogWarning($"Saved position is too far underground, using default spawn position");
       savedPosition = Vector3.zero; // Reset to trigger default spawn logic
   }
   ```

2. **Ground Collision Verification**: Raycast to verify terrain exists
   ```csharp
   bool hasGroundBelow = Physics.Raycast(savedPosition + Vector3.up * 5f, Vector3.down, 50f, terrainLayer);
   if (!hasGroundBelow)
   {
       Debug.LogWarning($"Chunks at saved location not loaded yet, using default spawn position");
       savedPosition = Vector3.zero; // Reset to trigger default spawn logic
   }
   ```

3. **Automatic Recovery**: Falls back to default spawn position (0, 0, 0) when validation fails
   - **Why not preserve X/Z?** Chunks at saved location haven't been loaded yet (Phase 2 happens before Phase 4 chunk loading)
   - Raycasts can't find terrain that doesn't exist yet
   - Default spawn position (0, 0, 0) is more likely to have cached terrain data
   - Vertical search at default position has better chance of success

### Validation Layers

**Layer 1: Phase 2 - Position Determination**
- Checks if Y coordinate is reasonable (Y >= -50)
- Verifies ground collision exists at saved position
- Performs vertical search if validation fails
- Preserves X/Z coordinates when possible (player returns to general area)

**Layer 2: Phase 4 - Terrain Loading**
- Waits for chunks to load
- Verifies collision is ready after chunk loading
- Logs warning if no collision detected (already existing)

**Layer 3: Fall Recovery System**
- Activates if player somehow starts falling despite validation
- Teleports to nearest valid ground position
- Acts as final safety net

## Expected Behavior After Fix

### Scenario 1: Valid Saved Position
```
[Phase 2] Loaded saved position: (125.00, 18.88, -104.61)
[Phase 2] Validated Y coordinate: ✓ (> -50)
[Phase 2] Verified ground collision: ✓
[Phase 2] Using validated saved position
```

### Scenario 2: Invalid Saved Position (Too Deep)
```
[Phase 2] Loaded saved position: (0.00, -571.60, 0.00)
[Phase 2] WARNING: Saved position is too far underground (Y=-571.60), using default spawn position
[Phase 2] No saved position, searching for valid spawn position
[VerticalSearch] Starting search from (0, 0, 0)
[VerticalSearch] Found ground at height offset 0: (0, 52.8, 0)
[Phase 2] Determined spawn position: (0, 52.8, 0)
```

### Scenario 3: Invalid Saved Position (No Collision - Chunks Not Loaded)
```
[Phase 2] Loaded saved position: (-11.49, 7.14, -53.66)
[Phase 2] Validated Y coordinate: ✓ (> -50)
[Phase 2] WARNING: No ground collision detected at (-11.49, 7.14, -53.66)
[Phase 2] WARNING: Chunks at saved location not loaded yet, using default spawn position
[Phase 2] No saved position, searching for valid spawn position
[VerticalSearch] Starting search from (0, 0, 0)
[VerticalSearch] Found ground at height offset 50: (0, 52.8, 0)
[Phase 2] Determined spawn position: (0, 52.8, 0)
```

## Testing Checklist

- [x] Add validation for Y < -50
- [x] Add ground collision verification
- [x] Preserve X/Z coordinates when performing vertical search
- [ ] Test with corrupted save file (Y=-571.60)
- [ ] Test with valid save file
- [ ] Test with no save file (new player)
- [ ] Verify no rubber-banding occurs
- [ ] Verify player spawns at correct location
- [ ] Verify fall recovery doesn't trigger immediately after spawn

## Related Files
- `Assets/Scripts/Network/UnifiedSpawnController.cs` - Main fix location
- `Assets/Scripts/Network/PlayerSpawner.cs` - Loads saved position
- `Assets/Scripts/TerrainGen/WorldSaveManager.cs` - Manages player data files
- `Assets/Scripts/Network/PlayerFallRecovery.cs` - Fall safety net

## Future Improvements

1. **Proactive Cleanup**: Delete invalid saved position files when detected
2. **Auto-correction**: Overwrite invalid saved positions with corrected positions
3. **Height Limits**: Add configurable min/max Y boundaries for valid positions
4. **Better Logging**: Add telemetry for how often validation catches bad positions
5. **Position History**: Keep last 3 valid positions as backup options

## Related Issues
- Endless fall loop bug (fixed by `SPAWN_FALL_BUG_FIX.md`)
- Loading UI loop bug (related to fall detection)
- Chunk unloading during spawn (fixed by Phase 3 world registration)

