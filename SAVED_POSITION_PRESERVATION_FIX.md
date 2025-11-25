# Saved Position Preservation Fix - November 25, 2025

## Problem: Valid Saved Positions Being Rejected

After implementing the timing fix (Phase 2 vs Phase 4), we discovered that **all** saved positions were being rejected and players were spawning at default position (0, 0, 0) instead of their last known location.

### What Was Happening

```
[WorldSaveManager] Successfully loaded data for player 0. Position: (-5.06, 6.29, -53.56)
[Phase 2] Saved position has no ground collision at (-5.06, 6.29, -53.56)
[Phase 2] Chunks at saved location not loaded yet, using default spawn position
[Phase 2] Using default spawn at safe height: (0.00, 60.00, 0.00)
```

**The Issue**: Phase 2 was doing a `Physics.Raycast()` to validate the saved position, but chunks at that location hadn't been loaded yet (that happens in Phase 4). The raycast always failed, so every saved position was rejected as "invalid".

The position `(-5.06, 6.29, -53.56)` was probably perfectly valid, but we couldn't verify it without loading chunks first.

## Solution: Y-Coordinate Validation Only in Phase 2

### New Strategy

**Phase 2** (Before Chunks Load):
- Only validate **Y coordinate bounds** (not too high, not too low)
- Don't do raycast validation (chunks don't exist yet!)
- Trust reasonable Y values (-50 to 300)
- Reject only obviously invalid positions

**Phase 4** (After Chunks Load):
- Load chunks at the saved X/Z location
- **NOW** do collision validation
- Adjust Y coordinate to actual ground level
- Use vertical search if needed

### Code Changes

#### Phase 2: Simplified Validation (Y-bounds only)

**Before** (rejected all positions):
```csharp
// Phase 2
if (savedPosition.y < -50f) {
    reject...
}
else {
    // Try raycast - ALWAYS FAILS because chunks not loaded
    bool hasGroundBelow = Physics.Raycast(...);
    if (!hasGroundBelow) {
        reject... // ← Rejected EVERY saved position
    }
}
```

**After** (trusts reasonable Y values):
```csharp
// Phase 2
if (savedPosition.y < -50f) {
    Debug.LogWarning("Too far underground");
    savedPosition = Vector3.zero;
}
else if (savedPosition.y > 300f) {
    Debug.LogWarning("Absurdly high");
    savedPosition = Vector3.zero;
}
else {
    // Y is reasonable - trust it! Verify in Phase 4
    return savedPosition; // ← Keep saved position!
}
```

#### Phase 4: Comprehensive Ground Finding

After chunks are loaded, verify and adjust the Y coordinate:

```csharp
// Phase 4 - After LoadSpawnTerrain()

// 1. Try raycasting DOWN (position might be above ground)
if (Physics.Raycast(position + Vector3.up * 5f, Vector3.down, out hit, 150f)) {
    targetPosition = hit.point + Vector3.up * 2f;
}
// 2. Try raycasting UP (position might be underground)
else if (Physics.Raycast(position, Vector3.up, out hit, 150f)) {
    targetPosition = hit.point + Vector3.up * 2f;
}
// 3. Use vertical search at this X/Z location
else {
    targetPosition = FindValidSpawnPositionWithVerticalSearch(position.x, 0, position.z);
}
```

## Benefits

1. **Preserves X/Z coordinates** - Player spawns near where they logged out
2. **Corrects Y coordinate** - Adjusts to actual ground level if needed
3. **No false rejections** - Doesn't reject valid positions due to missing chunks
4. **Works after terrain changes** - If terrain modified, finds new ground level
5. **Handles edge cases** - Underground spawns, high spawns, missing terrain

## Expected Flow

### Scenario 1: Valid Saved Position

**Player logged out at** `(-5.06, 6.29, -53.56)`

```
[Phase 2] Loaded saved position: (-5.06, 6.29, -53.56)
[Phase 2] Y coordinate reasonable (6.29 in range -50 to 300) ✓
[Phase 2] Using saved position for client 0: (-5.06, 6.29, -53.56)
[Phase 3] Registered player at (-5.06, 6.29, -53.56)
[Phase 4] Loading chunks around (-1, 0, -4)
[Phase 4] All chunks loaded
[Phase 4] Found ground at (-5.06, 6.52, -53.56), adjusting from 6.29 to 6.52
[Phase 5] Set position authority at (-5.06, 6.52, -53.56)
✅ Player spawns near last location!
```

### Scenario 2: Invalid Y Coordinate (Deep Underground)

**Player save file corrupted with** `(50, -571.60, 100)`

```
[Phase 2] Loaded saved position: (50, -571.60, 100)
[Phase 2] WARNING: Saved position too far underground (Y=-571.60)
[Phase 2] Using default spawn at safe height: (0, 60, 0)
[Phase 4] Loading chunks around (0, 3, 0)
[Phase 4] Found ground at (0, 52.86, 0)
[Phase 5] Set position authority at (0, 52.86, 0)
✅ Falls back to default spawn
```

### Scenario 3: Y Slightly Off (Terrain Modified Since Last Session)

**Player was at** `(20, 45, 30)` **but terrain now at** `Y=48`

```
[Phase 2] Using saved position: (20, 45, 30)
[Phase 4] Loading chunks around (1, 2, 2)
[Phase 4] Found ground at (20, 48, 30), adjusting from 45 to 48
[Phase 5] Set position authority at (20, 48, 30)
✅ X/Z preserved, Y adjusted to current terrain
```

## Validation Criteria

### Phase 2 Validation (Quick Bounds Check)

- ✅ **Accept** if `-50 < Y < 300`
- ❌ **Reject** if `Y < -50` (deep underground)
- ❌ **Reject** if `Y > 300` (absurdly high)
- ❌ **Reject** if `position == Vector3.zero` (no save file)

### Phase 4 Validation (After Chunks Loaded)

1. **Raycast DOWN** from `position + Vector3.up * 5`
   - If hit: Use hit point
2. **Raycast UP** from `position`
   - If hit: Use hit point (was underground)
3. **Vertical Search** at X/Z location
   - Search heights: 0, 50, 100, 150, -20, -50
   - If found: Use ground position
4. **Keep original position** (last resort)

## Testing Results

### Before Fix

```
❌ Saved position (-5.06, 6.29, -53.56) rejected
❌ Player spawns at default (0, 0, 0) every session
❌ Loses progress, has to travel back
```

### After Fix

```
✅ Saved position (-5.06, 6.29, -53.56) accepted
✅ Y adjusted to ground level: (-5.06, 6.52, -53.56)
✅ Player spawns where they logged out
✅ Smooth return-to-game experience
```

## Related Files

- `Assets/Scripts/Network/UnifiedSpawnController.cs` - Main changes
- `PHASE_2_VS_PHASE_4_FIX.md` - Timing issue explanation
- `HIGH_ALTITUDE_SPAWN_FIX.md` - Why not to raycast in Phase 2

## Key Insight

**Don't validate what you can't verify.**

Phase 2 can only validate Y-coordinate bounds (mathematical check). Collision validation requires chunks to exist, so defer it to Phase 4.

This allows us to:
- Trust saved positions with reasonable Y values
- Load chunks at the saved location
- Verify/adjust after terrain exists
- Preserve player's intended spawn location

## Important Update: Underground Spawn Fix

After implementing this fix, discovered another edge case: **deep underground mining tunnels**.

The initial Phase 4 logic would teleport players from underground tunnels to the surface. This was fixed by:

1. **Checking if position is in valid air space** (using `Physics.CheckSphere`)
2. **Only adjusting Y if player is stuck in solid blocks**
3. **Preserving underground positions** in caves/tunnels/mines

See `UNDERGROUND_SPAWN_FIX.md` for full details on this critical improvement.

