# Chunk Unload During Remesh Fix

## Issue Summary
Chunks were being unloaded while waiting for mesh regeneration after density modifications, causing "rough geometry with holes" - chunks would have their general shape applied but with incomplete/malformed geometry.

## Root Cause Analysis

### The Race Condition

1. **Player mines downward** into chunk (-1, -5, 10)
2. **202 density updates queued and processed** for the chunk (all targeting the same world position)
3. **After processing**, the chunk is:
   - ✅ Removed from `pendingDensityPointUpdates` (updates complete)
   - ✅ Added to `chunksNeedingMeshUpdate` queue (waiting for remesh)
   - ✅ Marked for save
   - ✅ Status changed to `Modified`

4. **Mesh regeneration is rate-limited** (`ProcessMeshUpdates` line 4803):
   ```csharp
   int maxUpdatesPerFrame = MeshDataPool.Instance.GetDynamicChunksPerFrame();
   ```
   Only a few chunks can regenerate their mesh per frame (e.g., 2-3 chunks)

5. **While waiting in the mesh queue**, the unload system checks eligibility:
   ```csharp
   // IsChunkEligibleForUnload checks:
   if (HasPendingUpdates(chunkCoord))  // Line 1623
       return false;
   ```

6. **`HasPendingUpdates()` returns FALSE** because:
   ```csharp
   hasPendingUpdates = pendingVoxelUpdates.ContainsKey(chunkCoord) || 
                       pendingDensityPointUpdates.ContainsKey(chunkCoord) ||
                       chunksWithPendingNeighborUpdates.Contains(chunkCoord) ||
                       forcedBoundaryChunks.Contains(chunkCoord);
   // ❌ Does NOT check chunksNeedingMeshUpdate!
   ```

7. **Unload system sees**:
   - ✓ No pending updates (`HasPendingUpdates()` = false)
   - ✓ Player moved away (mining downward, chunk now outside unload radius)
   - ✓ Chunk state is `Modified` (eligible for unload)
   - ✓ **CHUNK IS ELIGIBLE FOR UNLOAD!**

8. **Chunk unloads mid-mesh-regeneration** → Rough geometry with holes!

### Visual Evidence from Logs

```
Server received modification from client 0 for chunk (-1, -5, 10), voxel (4, 3, 15)
[ProcessPendingUpdates] Processing 202 density updates for loaded chunk (-1, -5, 10)
... (202 updates all for world pos -12.00, -77.00, 175.00) ...
[ProcessPendingUpdates] Queued mesh update for chunk (-1, -5, 10) after density changes
[ChunkData] SaveData invoked for (-1, -5, 10) (modified=True)
... (then chunk unloads before mesh regeneration completes) ...
```

### Why 202 Redundant Updates?

The logs show 202 density updates all targeting the **exact same world position** (-12.00, -77.00, 175.00). This is a secondary issue - the system appears to be queuing duplicate density modifications instead of consolidating them. This would explain:
- Long processing times for single mining actions
- Increased likelihood of the race condition (more time in queue = more chance of premature unload)
- Gradual density convergence rather than single-step calculation

## The Fix

Modified `HasPendingUpdates()` in `World.cs` (lines 5025-5036) to also check:
1. If chunk is in the `chunksNeedingMeshUpdate` queue
2. If chunk has an active `generationCoroutine`

```csharp
public bool HasPendingUpdates(Vector3Int chunkCoord)
{
    bool hasPendingUpdates = false;
    lock (updateLock)
    {
        hasPendingUpdates = pendingVoxelUpdates.ContainsKey(chunkCoord) || 
                        pendingDensityPointUpdates.ContainsKey(chunkCoord) ||
                        chunksWithPendingNeighborUpdates.Contains(chunkCoord) ||
                        forcedBoundaryChunks.Contains(chunkCoord);
    }
    
    // CRITICAL FIX: Also check if chunk is waiting for mesh regeneration or actively generating
    // This prevents unloading chunks that have finished processing density updates but are 
    // still queued for mesh regeneration (which is rate-limited per frame)
    if (!hasPendingUpdates && chunks.TryGetValue(chunkCoord, out Chunk chunk))
    {
        // Check if chunk is in the mesh update queue
        if (chunksNeedingMeshUpdate != null && chunksNeedingMeshUpdate.Contains(chunk))
        {
            hasPendingUpdates = true;
        }
        // Check if chunk has an active generation coroutine
        else if (chunk.generationCoroutine != null)
        {
            hasPendingUpdates = true;
        }
    }
    
    return hasPendingUpdates;
}
```

## What This Fixes

1. **Prevents premature unload** of chunks waiting for mesh regeneration
2. **Prevents premature unload** of chunks actively generating meshes
3. **Ensures complete mesh generation** before chunks become eligible for unload
4. **Eliminates holes in geometry** caused by interrupted mesh generation

## Testing Recommendations

1. **Mine deep underground** (Y < -32) and verify chunks maintain geometry
2. **Mine rapidly in multiple directions** to generate many mesh update requests
3. **Move away from mining area** to trigger unload system while mesh queue is full
4. **Check for "rough geometry with holes"** - should no longer occur
5. **Monitor logs** for:
   - `[SERVER] Unloading chunk` messages
   - `Queued mesh update for chunk` messages
   - Ensure no chunks unload while in mesh update queue

## Related Issues

### Secondary Issue: Redundant Density Updates
The logs show 202 identical density updates for a single mining action. This should be investigated separately as it:
- Increases processing overhead
- Makes the race condition more likely
- Suggests inefficiency in the density update queuing system

Potential locations to investigate:
- Where density updates are queued (check for duplicate queuing)
- Whether updates should be consolidated before processing
- If the 202 updates are intentional (iterative convergence) or a bug

### Observation: Double SaveData Calls
Some chunks show:
```
[ChunkData] SaveData invoked for (-1, -6, 10) (modified=True)
Skipping save for unmodified chunk (-1, -6, 10)
```

This suggests `SaveData()` is being called twice in quick succession, with `hasModifiedData` being cleared between calls. Investigation points:
- Line 4239: Save after density updates in `ProcessPendingUpdatesForChunk`
- Line 4272: Save after voxel updates in `ProcessPendingUpdatesForChunk`
- Line 4721: Batch save in `ProcessPendingUpdates`
- Line 2525: Save in terrain modification flow
- Check if these paths can execute twice for the same chunk

## Files Modified
- `Assets/Scripts/TerrainGen/World.cs`: Updated `HasPendingUpdates()` method

## Impact Assessment
- **Low Risk**: The fix is conservative - it only prevents unloading, doesn't change loading/generation logic
- **High Benefit**: Eliminates a critical bug causing visible holes in terrain
- **Performance**: Minimal impact - adds two quick checks to unload eligibility
- **Side Effects**: Chunks may stay loaded slightly longer, but only while actively regenerating


