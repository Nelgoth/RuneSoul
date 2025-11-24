# Terrain Cache Loading Crash Fix

## Problem

After implementing the terrain cache optimization (fixing 19,000 chunks queueing), the game started crashing during world loading at the LoadingSceneController stage.

## Root Cause

**Thread-Safety Violation**: The `recentlyAnalyzed` HashSet was being accessed from multiple threads without proper synchronization.

### The Issue

In my initial fix, I tried to optimize by checking `recentlyAnalyzed.Contains(coord)` BEFORE acquiring the lock:

```csharp
// PROBLEMATIC CODE (caused crash)
if (!wasModified && recentlyAnalyzed.Contains(coord))  // ← Outside lock!
{
    lock (cacheLock)
    {
        // ... check if changed ...
    }
}
```

**Why this crashed:**
- `HashSet<T>.Contains()` is NOT thread-safe
- During initial world load, hundreds/thousands of chunks generate simultaneously
- Multiple threads calling `SaveAnalysis()` concurrently
- Concurrent reads/writes to `recentlyAnalyzed` → collection modification exception or corruption
- Crash during loading when thousands of chunks try to save analysis

## The Fix

### Moved All HashSet Access Inside Lock

**Changed**: All access to `recentlyAnalyzed` now protected by `cacheLock`

```csharp
// FIXED CODE (thread-safe)
lock (cacheLock)
{
    // All HashSet operations safely inside lock
    if (analysisCache.TryGetValue(coord, out var existingData))
    {
        bool entryChanged = existingData.IsEmpty != isEmpty ||
                            existingData.IsSolid != isSolid ||
                            existingData.WasModified != wasModified;

        if (!entryChanged)
        {
            existingData.LastAnalyzedTicks = DateTime.UtcNow.Ticks;
            recentlyAnalyzed.Add(coord);  // ← Safe inside lock
            pendingDeleteCoords.Remove(coord);
            return; // Early exit without queueing for save
        }
    }

    // New or changed entry
    analysisCache[coord] = data;
    recentlyAnalyzed.Add(coord);  // ← Safe inside lock
    isDirty = true;
    pendingDeleteCoords.Remove(coord);
    pendingSaveCoords.Add(coord);
}
```

### Additional Improvements

1. **Throttled Diagnostic Logging**
   - Changed from every frame to every 60 frames
   - Prevents log spam during heavy loading

2. **Simplified Logic**
   - Removed double-lock pattern
   - Single lock acquisition per call
   - Clearer code flow

3. **Maintained Optimization**
   - Still prevents redundant saves (unchanged entries don't queue)
   - Still prevents periodic `recentlyAnalyzed.Clear()`
   - Thread-safe version of the optimization

## Testing Checklist

- [x] Code compiles without errors
- [ ] World loading doesn't crash
- [ ] Loading scene controller progresses normally
- [ ] No concurrent modification exceptions
- [ ] Still prevents 19,000 chunk queue spam
- [ ] Mining/building modifications still save correctly

## Technical Details

### Why HashSet Isn't Thread-Safe

From .NET documentation:
> A HashSet<T> can support multiple readers concurrently, as long as the collection is not being modified. Even so, enumerating through a collection is intrinsically not a thread-safe procedure.

During world loading:
- Thread A: Reads `recentlyAnalyzed.Contains()` → returns true
- Thread B: Modifies `recentlyAnalyzed.Add()` → collection modified
- Thread A: Continues with stale data or crashes with InvalidOperationException

### Lock Overhead vs Correctness

**Question**: Won't locking everything hurt performance?

**Answer**: No, because:
1. Locks are only held briefly (dictionary lookups, HashSet operations)
2. Correctness is more important than micro-optimizations
3. The real performance win is from NOT queueing 19,000 chunks, not from avoiding locks
4. Modern .NET locks are fast for uncontended scenarios

## Related Issues

- Original issue: 19,000 chunks queueing for cache save
- Previous fix: Removed periodic `recentlyAnalyzed.Clear()`  
- This fix: Made that solution thread-safe

## Files Modified

1. **Assets/Scripts/TerrainGen/TerrainAnalysisCache.cs**
   - Line 757-836: Rewrote `SaveAnalysis()` with proper locking
   - Line 337: Throttled diagnostic logging
   - All `recentlyAnalyzed` access now inside `cacheLock`

