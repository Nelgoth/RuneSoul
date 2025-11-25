# New World Double Loading Fix

## Problem Description

When creating a NEW world, the game was experiencing a "double loading" sequence:
1. Loading screen appears
2. Player spawns and becomes visible
3. Loading screen reappears (unexpected!)
4. Player falls and gets rescued
5. Finally spawns correctly

The user would see the loading UI twice and experience stuttering/falling behavior.

## Root Cause

**PlayerPrefs was storing player positions GLOBALLY across all worlds**, not per-world. When creating a new world:

1. **Wrong Position Loaded**: 
   - WorldSaveManager correctly returns `null` for the new world (no saved data)
   - PlayerSpawner falls back to PlayerPrefs with key `PlayerPosition_0`
   - This loads position from a **DIFFERENT/PREVIOUS world** (e.g., `-1.50, 27.49, 6.10`)

2. **Spawn in Mid-Air**:
   - That old position is mid-air in the NEW world's terrain
   - Player immediately starts falling

3. **Fall Recovery Triggers**:
   - Fall recovery system activates
   - Forces loading of 125+ chunks around rescue position
   - Triggers massive chunk generation (24,375 chunks in logs!)
   - Shows loading UI again

4. **Second Rescue**:
   - Player continues falling
   - Gets rescued again
   - Eventually settles

### Evidence from Logs

```
[PlayerSpawner] Found PlayerPrefs data: -1.502743,27.48695,6.0989
[PlayerSpawner] ✅ Loaded position from PlayerPrefs for client 0: (-1.50, 27.49, 6.10)
```
^^ This position is from a PREVIOUS world, not the new world!

```
[WorldSaveManager] ❌ No saved data file found for player 0
```
^^ WorldSaveManager correctly knows there's no data for new world

```
Player 0 has requested rescue from falling!
[PlayerFallRecovery] Forcing load of chunk (X, Y, Z) for rescue
... (125 chunks force-loaded)
Initial world load completed in 14.4s (24375 chunks).
```
^^ Fall recovery causes massive chunk loading

## Solution

**Made PlayerPrefs keys world-specific** by including the World ID:

### Before (WRONG):
```csharp
string key = $"PlayerPosition_{clientId}";  // Global across all worlds!
```

### After (CORRECT):
```csharp
string worldId = WorldSaveManager.Instance?.CurrentWorldId ?? "";
string key = string.IsNullOrEmpty(worldId) 
    ? $"PlayerPosition_{clientId}"              // Fallback (shouldn't happen)
    : $"PlayerPosition_{worldId}_{clientId}";   // World-specific!
```

### Key Changes

1. **GetClientLastPosition()** (line 230-256):
   - Now includes world ID in PlayerPrefs key
   - Only uses PlayerPrefs if world ID is valid
   - Prevents loading positions from wrong world

2. **SavePlayerPosition()** (line 270-282):
   - Now saves with world-specific key
   - Each world has its own saved positions
   - New worlds start fresh at spawn

## Benefits

✅ **New worlds spawn correctly** - No old positions loaded  
✅ **No double-loading** - Player spawns once at correct position  
✅ **No fall recovery on spawn** - Player starts on solid ground  
✅ **Reduced chunk loading** - Only loads necessary spawn area  
✅ **Per-world positions** - Each world maintains its own player data  

## Behavior Changes

### For NEW Worlds:
- ✅ WorldSaveManager returns `null` (correct)
- ✅ PlayerPrefs returns `null` (NEW - was incorrectly loading old data)
- ✅ Player spawns at default spawn position with ground detection
- ✅ Single clean loading sequence

### For EXISTING Worlds:
- ✅ WorldSaveManager loads from JSON (primary source)
- ✅ PlayerPrefs serves as world-specific backup
- ✅ Player spawns at last saved position
- ✅ No behavior change for existing saves

## Testing Recommendations

1. **New World Creation**:
   - Create a new world
   - Verify single loading screen
   - Verify spawn on solid ground
   - Verify no falling/rescue

2. **World Switching**:
   - Play in World A, save position
   - Create new World B
   - Verify World B doesn't use World A's position
   - Switch back to World A
   - Verify World A position preserved

3. **Multiple Players** (if applicable):
   - Test that each player in a world has separate positions
   - Verify keys are unique per world AND per client

## Additional Notes

### Chunk Pool Size Investigation

The logs showed `[ChunkPool] Awake called, initialPoolSize: 20341` which seems very high. This may indicate:
- TerrainConfigs has `autoCalculateChunkPoolSize = false` and `manualChunkPoolSize` set too high
- OR runtime calculation is producing unexpected values
- Consider auditing chunk pool settings separately

### Fall Recovery System

The fall recovery system is working as designed (rescuing falling players), but the root cause (spawning in wrong position) was triggering it unnecessarily. The fix prevents this cascade.

## Files Modified

- `Assets/Scripts/Network/PlayerSpawner.cs`:
  - `GetClientLastPosition()` - Added world ID to PlayerPrefs key
  - `SavePlayerPosition()` - Added world ID to PlayerPrefs key

## Related Systems

- **WorldSaveManager**: Primary save system (per-world JSON files) ✅ Already correct
- **PlayerPrefs**: Backup/fallback system (now world-specific) ✅ Fixed
- **UnifiedSpawnController**: Spawn coordination ✅ No changes needed
- **PlayerFallRecovery**: Safety system ✅ No changes needed


