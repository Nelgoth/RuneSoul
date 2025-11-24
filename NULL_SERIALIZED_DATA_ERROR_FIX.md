# Null Serialized Data Error Fix

## Problem

When loading a world from save, these errors appeared for multiple chunks:

```
Cannot load from null serialized data for chunk (0, 0, -3)
Cannot load from null serialized data for chunk (-1, 0, -3)
Cannot load from null serialized data for chunk (1, 0, -3)
... etc
```

The chunks would still load correctly, suggesting this was an order-of-operations issue rather than actual data corruption.

## Root Cause

The issue was in how `ChunkData.TryLoadData()` handled loading for different serialization formats.

### Binary Format (Current/Modern)
- `BinaryChunkSerializer.Deserialize()` writes data **directly to NativeArrays** (DensityPoints, VoxelData)
- It does **NOT populate** the serialized fields (serializedDensityValues, serializedVoxelStates, serializedVoxelHitpoints)
- These fields remain null because binary format doesn't use them

### JSON Format (Legacy)
- `JsonUtility.FromJsonOverwrite()` populates the serialized fields
- `LoadFromSerialization()` copies from serialized fields to NativeArrays
- Serialized fields exist and contain data

### The Problem

In `ChunkData.TryLoadData()` (line 366), the code **always** called `LoadFromSerialization()` after `SaveSystem.LoadChunkData()` returned:

```csharp
public bool TryLoadData()
{
    if (SaveSystem.LoadChunkData(ChunkCoordinate, this))
    {
        LogChunkIo($"[ChunkData] TryLoadData succeeded for {ChunkCoordinate}");
        EnsureArraysCreated();
        LoadFromSerialization();  // ← ALWAYS called, even for binary chunks!
        ValidateDensityPoints();
        ...
    }
}
```

For **binary-saved chunks**:
1. `BinaryChunkSerializer.Deserialize()` loads data directly to NativeArrays ✓
2. Serialized fields remain null (not used in binary format) ✓
3. `TryLoadData()` calls `LoadFromSerialization()` ✗
4. `LoadFromSerialization()` checks if serialized fields are null → **ERROR!**

The data was already loaded (step 1), so the error was harmless but noisy.

## The Fix

Changed `TryLoadData()` to only call `LoadFromSerialization()` if serialized fields exist:

```csharp
public bool TryLoadData()
{
    if (SaveSystem.LoadChunkData(ChunkCoordinate, this))
    {
        LogChunkIo($"[ChunkData] TryLoadData succeeded for {ChunkCoordinate}");
        EnsureArraysCreated();
        
        // CRITICAL FIX: Only call LoadFromSerialization if serialized fields exist
        // For binary format, data is already loaded directly into NativeArrays by BinaryChunkSerializer
        // For JSON format, SaveSystem.LoadChunkData already calls LoadFromSerialization
        // This check prevents "Cannot load from null serialized data" errors for binary-saved chunks
        if (serializedDensityValues != null && serializedVoxelStates != null && serializedVoxelHitpoints != null)
        {
            LoadFromSerialization();
        }
        
        ValidateDensityPoints();
        ...
    }
}
```

Also added clarifying comments to `LoadFromSerialization()`:

```csharp
public void LoadFromSerialization()
{
    // CRITICAL: This method is only for JSON deserialization
    // Binary deserialization writes directly to NativeArrays and doesn't populate these fields
    // TryLoadData now checks if serialized fields exist before calling this method
    if (serializedDensityValues == null || serializedVoxelStates == null || serializedVoxelHitpoints == null)
    {
        Debug.LogError($"Cannot load from null serialized data for chunk {chunkCoordinate}");
        return;
    }
    ...
}
```

## How It Works Now

### Loading Binary-Saved Chunks (Modern Format)
1. `SaveSystem.LoadChunkData()` calls `BinaryChunkSerializer.Deserialize()`
2. Binary deserializer writes directly to NativeArrays
3. Serialized fields remain null (as expected)
4. `TryLoadData()` checks if serialized fields exist → **they don't, skip LoadFromSerialization()**
5. Data is already loaded, validation continues
6. **No error message!**

### Loading JSON-Saved Chunks (Legacy Format)
1. `SaveSystem.LoadChunkData()` calls `JsonUtility.FromJsonOverwrite()`
2. Serialized fields are populated
3. `SaveSystem.LoadChunkData()` calls `LoadFromSerialization()` to copy to NativeArrays
4. `TryLoadData()` checks if serialized fields exist → **they do, but LoadFromSerialization() already ran**
5. Calling it again is redundant but harmless (data gets copied twice)
6. Works correctly

## Why This Wasn't Breaking Things

- The error occurred **after** the data was already successfully loaded by the binary deserializer
- `LoadFromSerialization()` would return early when it detected null fields
- Subsequent code (`ValidateDensityPoints()`, etc.) worked fine because the NativeArrays were already populated
- The error was purely cosmetic/diagnostic

## Expected Behavior After Fix

- **Loading binary-saved chunks**: No errors, data loads silently
- **Loading JSON-saved chunks**: Works as before (backward compatible)
- **Loading modified chunks**: Works correctly for both formats
- **Console**: Clean, no spurious error messages

## Files Modified

- `Assets/Scripts/TerrainGen/ChunkData.cs`
  - Lines 367-374: Added null check before calling LoadFromSerialization()
  - Lines 438-441: Added clarifying comments to LoadFromSerialization()

## Technical Notes

### Why Two Serialization Paths?

The game uses two serialization formats:

1. **Binary Format** (current, lines ~140-258 in BinaryChunkSerializer.cs)
   - Fast, compact, direct to NativeArrays
   - Used for all new saves
   - Magic number: 0x434E4B42 ("CKNB")

2. **JSON Format** (legacy, handled by Unity's JsonUtility)
   - Slower, larger files
   - Uses intermediate serialized fields
   - Kept for backward compatibility with old saves

The code auto-detects which format a file uses (see `SaveSystem.LoadChunkData()` line 399-402).

### Why Not Call LoadFromSerialization for JSON in TryLoadData?

Because `SaveSystem.LoadChunkData()` already calls it (line 420 in SaveSystem.cs) for JSON chunks. The call in `TryLoadData()` was redundant for JSON and incorrect for binary.

### Future Improvement

Could add a flag to `ChunkData` to track whether data was loaded via binary or JSON format, making the logic more explicit. But the null check is simpler and works for both cases.

