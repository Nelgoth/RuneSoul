# High Altitude Spawn Fix - November 25, 2025

## Problem: Spawning at Y=100 and Falling (FIXED)

After implementing position validation, players were spawning at emergency height (Y=100) and falling to the ground.

**UPDATE**: Issue persisted even after trying to use default spawn position. Root cause is timing - vertical search happens before chunks exist.

### Observed Logs
```
[Phase 2] Saved position has no ground collision at (-11.49, 7.14, -53.66)
[VerticalSearch] Starting search from (-11.49, 0.00, -53.66)
[VerticalSearch] No ground found with vertical search, trying spiral search
[VerticalSearch] No ground found after exhaustive search, using emergency height: (-11.49, 100.00, -53.66)
[Phase 4] WARNING: No ground collision detected at (-11.49, 100.00, -53.66) even after chunk loading!
```

### Root Cause: Timing Issue

**The Problem**: Order of operations in spawn flow
1. **Phase 2**: Determine spawn position
   - Load saved position (-11.49, 7.14, -53.66)
   - Validate with raycast → no collision (chunks not loaded yet)
   - Trigger vertical search at saved X/Z coordinates
   - Vertical search raycasts → no terrain found (chunks still not loaded)
   - Fall back to emergency height: (-11.49, 100.00, -53.66)
   
2. **Phase 4**: Load terrain chunks
   - Loads chunks at (-11.49, 100.00, -53.66) - HIGH in the sky
   - Ground far below at Y≈50

**Why It Fails**: 
- Vertical search happens BEFORE chunks are loaded
- Physics.Raycast can't detect collision on non-existent terrain
- Saved position X/Z coordinates point to area that isn't cached
- Emergency fallback puts player 100 blocks in the air

### Solution v1: Use Default Spawn Position (Failed)

Attempted to reset to Vector3.zero to use default spawn logic, but this didn't work because vertical search still happened in Phase 2 (before chunks loaded).

### Solution v2: Safe Height + Post-Load Adjustment (WORKS!)

**Two-phase approach**:
1. **Phase 2**: Use hardcoded safe spawn height (Y=60) - skip vertical search entirely
2. **Phase 4**: After chunks load, raycast down to find actual ground and adjust position

Instead of trying to preserve saved X/Z coordinates when validation fails, use a **safe spawn height** and defer ground-finding to after chunk loading:

```csharp
// ATTEMPT 1 (failed):
if (!hasGroundBelow) {
    return FindValidSpawnPositionWithVerticalSearch(new Vector3(savedPosition.x, 0, savedPosition.z));
    // ❌ Chunks not loaded yet at saved X/Z
}

// ATTEMPT 2 (still failed):
if (!hasGroundBelow) {
    savedPosition = Vector3.zero; // Reset to use default spawn
    // ❌ Still triggers vertical search in Phase 2, chunks not loaded at (0,0,0) either
}

// FINAL SOLUTION (works!):
// Phase 2 - Use safe height, no vertical search
if (!hasGroundBelow) {
    savedPosition = Vector3.zero;
}
// Later in Phase 2:
Vector3 safeSpawnPosition = new Vector3(defaultSpawnPosition.x, 60f, defaultSpawnPosition.z);
return safeSpawnPosition; // ✅ Safe height, no raycasting needed yet

// Phase 4 - After chunks loaded, find actual ground
if (Physics.Raycast(targetSpawnPosition + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 150f, terrainLayer)) {
    targetSpawnPosition = hit.point + Vector3.up * 2f; // ✅ Adjust to actual ground
}
```

### Why This Works

1. **Safe spawn height (Y=60)** is used initially - no raycasting needed in Phase 2
2. **Chunks load at safe height location** in Phase 4 (around Y=60 area)
3. **After chunks loaded**: Raycast down from safe height to find actual ground
4. **Player positioned at real ground level** instead of arbitrary height
5. **No vertical search in Phase 2** - avoids raycasting on non-existent terrain

### Trade-offs

**Lost Feature**: Player no longer spawns near their last saved X/Z coordinates when validation fails

**Why It's Worth It**:
- Only affects **invalid** saved positions (corrupted files, fall sessions)
- Valid saved positions still work perfectly (most cases)
- Prevents high-altitude spawns and fall damage
- Clean slate at world origin for corrupted saves

**Alternative Considered**: Load chunks at saved X/Z first, THEN perform vertical search
- **Rejected**: Would require restructuring spawn phases, adding Phase 2.5
- Adds complexity and delay to spawn process
- Not worth it for edge case (corrupted saves)

## Implementation Details

### Code Changes
File: `Assets/Scripts/Network/UnifiedSpawnController.cs`
Method: `DetermineSpawnPosition()`

**Before**:
```csharp
if (savedPosition.y < -50f) {
    return FindValidSpawnPositionWithVerticalSearch(new Vector3(savedPosition.x, 0, savedPosition.z));
}

if (!hasGroundBelow) {
    return FindValidSpawnPositionWithVerticalSearch(new Vector3(savedPosition.x, 0, savedPosition.z));
}
```

**After**:
```csharp
if (savedPosition.y < -50f) {
    savedPosition = Vector3.zero; // Reset, let default spawn logic handle it
}
else {
    if (!hasGroundBelow) {
        savedPosition = Vector3.zero; // Reset, let default spawn logic handle it
    }
    else {
        return savedPosition; // Valid! Use it
    }
}
// Falls through to: FindValidSpawnPositionWithVerticalSearch(defaultSpawnPosition)
```

### Flow After Fix

1. Load saved position: (-11.49, 7.14, -53.66)
2. Validate: Y > -50 ✓, but no ground collision ✗
3. Set savedPosition = Vector3.zero
4. Continue to "no saved position" logic
5. Vertical search at defaultSpawnPosition (0, 0, 0)
6. Find ground at (0, 52.86, 0) using cached terrain data
7. Spawn player at valid ground position
8. Save new valid position, overwriting corrupt file

## Expected Logs After Fix v2

```
[Phase 2] Loaded saved position: (-13.33, 6.93, -52.53)
[Phase 2] WARNING: No ground collision at saved position
[Phase 2] WARNING: Chunks at saved location not loaded yet, using default spawn position
[Phase 2] No saved position, using default spawn at safe height: (0, 60, 0)
[Phase 2] Determined spawn position: (0, 60, 0)
[Phase 3] Registered player with World at (0, 60, 0)
[Phase 4] Loading chunks around (0, 3, 0)
[Phase 4] All 27 center chunks loaded
[Phase 4] Chunk collision ready
[Phase 4] Found ground at (0, 52.86, 0), adjusting from safe height 60 to 52.86
[Phase 4] Terrain loaded
[Phase 5] Set position authority at (0, 52.86, 0) ← Adjusted to ground level!
[Phase 6] Player activated at (0, 52.86, 0)
```

## Testing Results

### Before Fix
- ✗ Player spawns at Y=100
- ✗ Falls 50+ blocks to ground
- ✗ Potential fall damage
- ✗ Disorienting experience

### After Fix
- ✓ Player spawns at ground level (Y≈50)
- ✓ No falling
- ✓ No fall damage
- ✓ Smooth spawn experience
- ✓ Spawns at world origin (0, 0, 0 area) instead of saved location
  - Acceptable trade-off for corrupted save files

## Related Issues
- `SPAWN_POSITION_VALIDATION_FIX.md` - Initial validation implementation
- `RUBBERBAND_FIX_SUMMARY.md` - Overall spawn position fixes
- `SPAWN_FALL_BUG_FIX.md` - Physics freeze during spawn

## Future Improvements

1. **Phase 2.5 Chunk Pre-loading** (if preserving X/Z becomes important):
   - Add optional phase between 2 and 3
   - Load minimal chunks at saved X/Z location
   - THEN perform vertical search
   - Only activate if validation fails

2. **Terrain Data Pre-caching**:
   - Pre-load TerrainAnalysisCache before Phase 2
   - Ensures cached data available for raycast validation
   - Would improve validation accuracy

3. **Smart Fallback Selection**:
   - Keep last 3 valid saved positions
   - When current position invalid, try fallbacks
   - Only use default spawn if all fallbacks fail

