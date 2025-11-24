# World Loading Path Bug Fix

## Problem

When loading a saved world with modifications, the game would hang at "70% Finalizing world" because the `SaveSystem` was creating a modification log for the wrong world path.

## Root Cause

**Timing/Ordering Bug in `WorldSaveManager.LoadWorld()`:**

The method was calling `SaveSystem.ResetPathCache()` BEFORE updating the `worldSaveFolder` field:

```csharp
// OLD CODE (BROKEN):
// Reset caches when changing worlds
if (currentWorldId != worldId)
{
    SaveSystem.ResetPathCache();  // ❌ Uses OLD path (default_world)
}

currentWorldId = worldId;
worldSaveFolder = targetFolder;  // ✅ Updates path AFTER reset
```

When `SaveSystem.ResetPathCache()` was called:
1. It read `WorldSaveManager.Instance.WorldSaveFolder` (still pointing to "default_world")
2. Created a `ChunkModificationLog` for "default_world"
3. **THEN** WorldSaveManager updated its fields to the new world

Result: The modification log was created for the wrong world, causing file access conflicts and hangs.

## Solution

**Update fields BEFORE calling ResetPathCache():**

```csharp
// NEW CODE (FIXED):
// Check if we're changing worlds BEFORE updating the fields
bool isChangingWorlds = (currentWorldId != worldId);

// UPDATE FIELDS FIRST before resetting caches
// This is critical - SaveSystem.ResetPathCache() needs the NEW path
currentWorldId = worldId;
worldSaveFolder = targetFolder;
worldMetadataPath = Path.Combine(worldSaveFolder, "world.meta");

// Reset caches when changing worlds (now uses the updated worldSaveFolder)
if (isChangingWorlds)
{
    Debug.Log($"[WorldSaveManager] Resetting SaveSystem caches for new world: {worldId}");
    SaveSystem.ResetPathCache();  // ✅ Now uses CORRECT path
}
```

## Evidence from Logs

**Before Fix:**
```
[Start Game] Selected world: AAA
[WorldSaveManager] Attempting to load world: f4ae32b2-4713-4b27-a224-ca07593dd7ff
[SaveSystem] Creating new modification log for: C:/Users/.../default_world  ❌ WRONG PATH
```

**After Fix (expected):**
```
[Start Game] Selected world: AAA
[WorldSaveManager] Attempting to load world: f4ae32b2-4713-4b27-a224-ca07593dd7ff
[SaveSystem] Creating new modification log for: C:/Users/.../f4ae32b2-4713-4b27-a224-ca07593dd7ff  ✅ CORRECT PATH
```

## Files Modified

- `Assets/Scripts/TerrainGen/WorldSaveManager.cs` - Fixed field update ordering in `LoadWorld()`

## Related Issues

This fix resolves:
- Game hanging at 70% when loading worlds with modifications
- File sharing violations on chunk_modifications.log
- Modifications not being applied to the correct world




