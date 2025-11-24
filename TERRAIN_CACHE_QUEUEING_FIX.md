# Terrain Cache Excessive Queueing Fix

## Problem

When moving or looking around in-game, the terrain cache system was queueing **16,000+ chunks** for caching at once, even when only a small number of new chunks came into view.

## Root Cause

The issue was in `TerrainAnalysisCache.cs` in the `Update()` method:

**Lines 378-384 (before fix):**
```csharp
// Check if it's time for a regular save
if (!synchronousFlushMode && Time.time - lastSaveTime > SAVE_INTERVAL && isDirty)
{
    QueueFullSave();
    // DON'T clear recentlyAnalyzed - it prevents redundant saves
}
```

**What `QueueFullSave()` was doing:**
- Iterating through **EVERY chunk** in the `analysisCache` dictionary
- Adding ALL of them to `pendingSaveCoords` 
- This happened **every 30 seconds** (SAVE_INTERVAL)

**The Impact:**
- The `analysisCache` can hold up to 100,000 chunks (MAX_CACHE_SIZE)
- After playing for a while, you might have 10,000-20,000+ chunks in memory
- Every 30 seconds, ALL of these chunks were being queued for saving
- This caused massive spikes in the save queue (16K+ chunks)
- These weren't even new or changed chunks - they were just being re-queued

## The Fix

### 1. Removed QueueFullSave() from Periodic Saves

Changed the periodic save logic to NOT call `QueueFullSave()`:

```csharp
// Check if it's time for a regular save
// CRITICAL FIX: Don't call QueueFullSave() for periodic saves!
// SaveAnalysis() already adds changed chunks to pendingSaveCoords
// QueueFullSave() was queueing ALL chunks (potentially 10K-100K) every 30 seconds
if (!synchronousFlushMode && Time.time - lastSaveTime > SAVE_INTERVAL && isDirty)
{
    // Just log and reset the timer - chunks are already queued via SaveAnalysis()
    if (pendingSaveCoords.Count > 0)
    {
        LogMessage($"Periodic save: {pendingSaveCoords.Count} chunks pending", LogLevel.Info);
    }
    
    isDirty = false;
    lastSaveTime = Time.time;
}
```

### 2. Clarified QueueFullSave() Purpose

Added documentation to `QueueFullSave()` to clarify it's only for forced synchronization:

```csharp
private static void QueueFullSave()
{
    // This method is only used for forced synchronization (ForceSynchronize, OnApplicationQuit)
    // NOT for periodic saves (which now just use pendingSaveCoords directly)
    lock (cacheLock)
    {
        foreach (var coord in analysisCache.Keys)
        {
            if (!pendingSaveCoords.Contains(coord))
            {
                pendingSaveCoords.Add(coord);
            }
        }
    }
    
    LogMessage($"Queued full save of {analysisCache.Count} terrain analyses", LogLevel.Info);
    isDirty = false;
    lastSaveTime = Time.time;
}
```

## How It Works Now

### Normal Operation (During Gameplay)
1. When a chunk is analyzed, `SaveAnalysis()` is called
2. If the chunk is new or changed, it's added to `pendingSaveCoords`
3. Every 30 seconds, the system just logs how many chunks are pending
4. Only chunks that actually changed get saved

### Forced Synchronization (Shutdown/Explicit Save)
1. `QueueFullSave()` is called explicitly
2. ALL chunks in the cache are queued for saving
3. This ensures nothing is lost on shutdown

## Expected Behavior After Fix

- **Normal movement/looking around**: Only newly analyzed chunks (10-100s) get queued
- **Periodic saves every 30 seconds**: Only chunks that changed since last save
- **No more 16K+ spikes**: Queue size should stay proportional to actual exploration
- **Shutdown/world change**: Still saves everything to ensure no data loss

## Testing

To verify the fix:
1. Load a world you've already explored extensively
2. Move around and look in different directions
3. Check the console for terrain cache save queue messages
4. You should see queue sizes in the 10s-100s, not 1000s-10000s
5. Only when loading completely new areas should you see larger queue sizes

## Technical Notes

### Why This Wasn't Caught Earlier
- The system was designed to handle large batches (BATCH_SIZE = 250, ACCELERATED_BATCH_SIZE = 8192)
- The massive queue was being processed, but inefficiently
- It wasn't causing crashes, just unnecessary disk I/O and memory churn
- The periodic "full save" was meant as a safety mechanism, but was too aggressive

### Why SaveAnalysis() Is Sufficient
- `SaveAnalysis()` is called every time a chunk is analyzed (see `ChunkData.QuickTerrainCheck()`)
- It already checks if the chunk data changed (line 850-861)
- If unchanged, it returns early without queuing for save
- If changed or new, it adds to `pendingSaveCoords`
- This is the correct granular approach - only save what changed

### The isDirty Flag
- `isDirty` is set to `true` whenever `SaveAnalysis()` adds a chunk to `pendingSaveCoords`
- Previously, it triggered `QueueFullSave()` every 30 seconds
- Now it just triggers a timer reset and log message
- The actual saves happen through the normal `ProcessPendingSavesInternal()` flow

## Files Modified

- `Assets/Scripts/TerrainGen/TerrainAnalysisCache.cs`
  - Lines 378-394: Changed periodic save logic
  - Lines 717-720: Added clarifying comments to QueueFullSave()

## Related Issues

This fix addresses:
- Excessive disk I/O during normal gameplay
- Unnecessary memory churn from processing large save queues
- Performance hitches every 30 seconds when the queue was processed
- Confusion about why so many chunks were being "cached" when nothing changed

