# Underground Spawn Fix - November 25, 2025

## Problem: Mining Tunnels Not Preserved

User discovered a critical edge case: **Players mining deep underground would get teleported to the surface on respawn**.

### The Scenario

1. Player mines deep underground to Y=-30 in a tunnel
2. Logs out at position `(-5, -30, -53)`
3. Logs back in

**OLD Behavior (Bug)**:
```
[Phase 2] Accept position Y=-30 (> -50 threshold) ✓
[Phase 4] Load chunks at (-5, -30, -53)
[Phase 4] Raycast DOWN → no hit (air below in tunnel)
[Phase 4] Raycast UP → hit surface at Y=50
[Phase 4] Adjust position to (-5, 52, -53) ← Teleported to surface!
```

**Result**: Player's mining progress lost, teleported out of their tunnel! 😱

## Root Cause

The Phase 4 "raycast UP" logic was designed to handle players spawning **inside solid blocks**, but it also triggered when they were in **valid underground air spaces** (tunnels, caves, mines).

The code couldn't distinguish between:
- **Invalid**: Player stuck inside solid terrain (need to move them)
- **Valid**: Player in underground tunnel/cave (should stay there)

## Solution: Check if Position is in Valid Air Space

### New Logic Flow

**Step 1**: Check if spawn position is in valid air space
```csharp
bool isInAirSpace = !Physics.CheckSphere(targetSpawnPosition, 0.3f, terrainLayer);
```

**Step 2A**: If in **valid air space** (tunnel/cave/above ground):
- Try raycasting down to find nearby ground
- Only adjust if **more than 3 blocks above ground**
- Otherwise **keep the position** (player is in tunnel/cave)

**Step 2B**: If **inside solid terrain** (stuck in blocks):
- Raycast UP to find surface
- Move player to surface + 2m
- Only happens when actually stuck in blocks

### Code Changes

**File**: `Assets/Scripts/Network/UnifiedSpawnController.cs`, Phase 4

**OLD** (teleported miners to surface):
```csharp
// If no ground below, try raycasting up
else if (Physics.Raycast(position, Vector3.up, out hit, 150f)) {
    // Teleports to surface even if in valid tunnel!
    targetPosition = hit.point + Vector3.up * 2f;
}
```

**NEW** (preserves underground locations):
```csharp
// Check if position is in valid air space
bool isInAirSpace = !Physics.CheckSphere(position, 0.3f, terrainLayer);

if (isInAirSpace) {
    // Valid air space - keep position unless floating high
    if (Raycast DOWN finds ground within 20m) {
        if (more than 3m above ground) {
            adjust to ground; // Fix floating spawns
        } else {
            keep position; // Underground tunnel/cave or near ground
        }
    } else {
        keep position; // Deep mine, no immediate ground
    }
} else {
    // INSIDE solid blocks - need to rescue player
    Raycast UP to find surface;
    Move to surface;
}
```

## Expected Behavior After Fix

### Scenario 1: Mining Tunnel (Deep Underground)

**Player at** `(-5, -30, -53)` in underground tunnel

```
[Phase 2] Using saved position: (-5, -30, -53)
[Phase 4] Loading chunks
[Phase 4] Position in air space ✓
[Phase 4] No immediate ground below (deep cave/mine)
[Phase 4] Keeping position at (-5, -30, -53) ✓
[Phase 5] Set position authority at (-5, -30, -53)
✅ Player spawns in their tunnel where they left off!
```

### Scenario 2: Floating Above Ground

**Player at** `(10, 60, 20)` (10 blocks above ground at Y=50)

```
[Phase 2] Using saved position: (10, 60, 20)
[Phase 4] Position in air space ✓
[Phase 4] Found ground at (10, 50, 20)
[Phase 4] Player 10m above ground, adjusting from 60 to 52
[Phase 5] Set position authority at (10, 52, 20)
✅ Player adjusted to ground level (prevented fall)
```

### Scenario 3: Stuck Inside Solid Terrain

**Player at** `(0, 45, 0)` **inside solid mountain**

```
[Phase 2] Using saved position: (0, 45, 0)
[Phase 4] Position is INSIDE solid terrain ✗
[Phase 4] WARNING: Saved position is inside solid terrain
[Phase 4] Raycasting up to find surface
[Phase 4] Moving player to surface at (0, 72, 0)
[Phase 5] Set position authority at (0, 72, 0)
✅ Player rescued to surface (was genuinely stuck)
```

### Scenario 4: Shallow Underground Cave

**Player at** `(20, 48, 30)` in shallow cave (ground at Y=50)

```
[Phase 2] Using saved position: (20, 48, 30)
[Phase 4] Position in air space ✓
[Phase 4] Ground 2m below at (20, 50, 30)
[Phase 4] Distance to ground: 2m (< 3m threshold)
[Phase 4] Position verified in valid air space (underground tunnel/cave)
[Phase 5] Set position authority at (20, 48, 30)
✅ Player stays in their cave!
```

## Key Parameters

### Air Space Detection
```csharp
Physics.CheckSphere(position, 0.3f, terrainLayer)
```
- **Radius**: 0.3m (player collision size)
- **Returns true** if overlapping solid terrain
- **Returns false** if in air space

### Ground Proximity Threshold
```csharp
if (distanceToGround > 3f)
```
- **3 blocks** = threshold for "floating"
- Less than 3 blocks = acceptable (player in cave/near ground)
- More than 3 blocks = adjust to ground (prevent falling)

### Downward Raycast Range
```csharp
Physics.Raycast(..., Vector3.down, out hit, 20f, ...)
```
- **20 blocks** = search distance for ground below
- Enough to find ground in most caves
- Not so far that deep mines trigger adjustment

## Benefits

1. ✅ **Preserves mining tunnels** - Players spawn where they logged out underground
2. ✅ **Prevents surface teleportation** - No more losing mining progress
3. ✅ **Handles caves correctly** - Natural caves preserved
4. ✅ **Fixes floating spawns** - Players more than 3 blocks above ground adjusted down
5. ✅ **Rescues stuck players** - Players inside solid blocks moved to surface
6. ✅ **Distinguishes air space from solid** - Knows difference between tunnel and stuck

## Edge Cases Handled

| Situation | Y Coordinate | Air Space? | Action |
|-----------|--------------|------------|--------|
| Deep mine tunnel | -30 | ✓ Yes | Keep position |
| Shallow cave | 48 | ✓ Yes | Keep position |
| Floating high | 60 (ground at 50) | ✓ Yes | Adjust to ground |
| Stuck in mountain | 45 | ✗ No | Move to surface |
| Above ground | 52 | ✓ Yes | Keep or adjust to 50 |

## Testing Checklist

- [ ] Mine deep underground (Y < 0), logout, login → Should spawn in tunnel
- [ ] In shallow cave (Y ≈ surface level), logout, login → Should spawn in cave
- [ ] Floating high above ground, logout, login → Should adjust to ground
- [ ] Use debug to set position inside solid block → Should move to surface
- [ ] Above ground normally → Should spawn on ground

## Related Files

- `Assets/Scripts/Network/UnifiedSpawnController.cs` - Main fix
- `SAVED_POSITION_PRESERVATION_FIX.md` - Overall position validation
- `PHASE_2_VS_PHASE_4_FIX.md` - Timing of validation

## Design Philosophy

**Trust player positions in valid air spaces.**

If the player logged out in an underground tunnel, they probably want to continue mining there. Only intervene if they're genuinely stuck in solid terrain or floating dangerously high.

This respects player agency and preserves their gameplay progress.

