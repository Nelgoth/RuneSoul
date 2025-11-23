# Terrain Optimization Diagnostics - User Guide

## Quick Start

1. **Add the diagnostic component** to your World GameObject:
   - In Unity, select the GameObject with the `World` component
   - Add Component → `TerrainOptimizationDiagnostics`

2. **Press F3 in Play Mode** to toggle the diagnostic overlay

3. **Watch for warnings** in yellow/red indicating problems

## What the Diagnostics Show

### Performance Section
- **FPS**: Current, minimum, and maximum FPS over last 60 frames
- **Status**: Performance rating (EXCELLENT/GOOD/POOR/CRITICAL)
  - ✅ EXCELLENT: 55+ FPS
  - ⚠️ GOOD: 45-54 FPS
  - ⚠️ POOR: 30-44 FPS
  - 🔴 CRITICAL: <30 FPS

### Save System Section
- **Format**: Should show `BinaryCompressed` for best performance
- **Pending Saves**: Number of chunks waiting to be saved
  - ⚠️ Warning if not using BinaryCompressed

### World System Section
- **Loaded Chunks**: Current number of chunks in memory
- **Loaded/sec**: Chunks loaded (+) and unloaded (-) per second
- **Load Radius**: Distance chunks are loaded (in chunks and meters)
- **Unload Radius**: Distance chunks are unloaded
- **Chunks/Frame**: Max operations processed per frame
- **Target FPS**: FPS target for adaptive systems

### Chunk Pool Section
- **Available**: Chunks ready for reuse in the pool
  - ⚠️ Warning if < 10 (pool running low)

### Operations Queue Section
- **Pending Ops**: Chunk operations waiting to be processed
  - ⚠️ Warning if > 100 (backlog forming)

### Mesh Data Pool Section
- **Memory**: Current vs maximum mesh cache memory
- **Usage**: Percentage of max memory used
  - ⚠️ Warning if > 80% (high memory pressure)

### Terrain Cache Section
- **Pending Analysis**: Terrain analysis saves waiting
  - ⚠️ Warning if > 50 (cache backlog)

### LOD System Section
- **Enabled**: Whether LOD is active
- **LOD0-3**: Distance thresholds for each level
  - ⚠️ Warning if LOD max < Load radius (causes flickering)

### Recommendations Section
Actionable suggestions based on detected issues

## Common Problems & Solutions

### Problem: FPS Drops to 20-30 When Moving

**Possible Causes:**

1. **SaveSystem Not Using Binary Format**
   - Look for: "⚠ WARNING: Not using BinaryCompressed!"
   - **Solution**: Open `TerrainConfigs.asset`, set:
     ```
     useBinaryFormat = true
     compressChunkData = true
     useAsyncIO = true
     ```

2. **Chunk Pool Exhaustion**
   - Look for: "⚠ WARNING: Pool running low!"
   - **Solution**: Increase `chunkPoolBufferMultiplier` in TerrainConfigs from 1.1 to 1.5

3. **Operation Backlog**
   - Look for: "⚠ WARNING: Large operation backlog!"
   - **Solutions**:
     - Reduce `LoadRadius` (try 15 instead of 20)
     - Increase `ChunksPerFrame` (try 150 instead of 100)
     - Ensure `TargetFPS` matches your monitor (60 or 120)

4. **High Memory Pressure**
   - Look for: "⚠ WARNING: High memory pressure!"
   - **Solutions**:
     - Enable LOD system
     - Reduce `LoadRadius`
     - Increase `MaxMeshCacheSize` in TerrainConfigs

5. **LOD/Load Radius Mismatch**
   - Look for: "⚠ WARNING: LOD max (Xm) < Load radius (Ym)!"
   - **Solution**: Adjust `lodDistances` to match or exceed load radius:
     ```
     For LoadRadius = 20 (320m):
     lodDistances = [160, 240, 280, 320]
     ```

6. **SaveSystem Not Initialized**
   - **Solution**: Add this to your World initialization:
     ```csharp
     if (Config != null)
     {
         Config.InitializeSaveSystem();
     }
     ```

### Problem: Chunks Flickering On/Off

**Likely Causes:**
- LOD distances too small for Load radius
- Unload radius too close to Load radius

**Solutions:**
1. Disable LOD temporarily: `enableLOD = false`
2. If keeping LOD enabled, match distances to load radius
3. Increase gap between Load and Unload radius:
   ```
   LoadRadius: 20
   UnloadRadius: 24  (was 21, increase buffer)
   ```

### Problem: Slow Chunk Loading

**Check For:**
- Format not set to `BinaryCompressed`
- `useAsyncIO = false` (should be true)
- Large `Pending Ops` count
- Low `Chunks/Frame` value

**Solutions:**
1. Enable all optimizations in TerrainConfigs
2. Increase `ChunksPerFrame` if FPS is good
3. Check Unity console for errors during loading

## Advanced Diagnostics

### Enable Debug Logging

In `TerrainConfigs.asset`, enable specific logs:

```
enableChunkIOLogs = true          // See save/load operations
enableChunkStateLogs = true       // See state transitions
enableChunkLifecycleLogs = true   // See chunk load/unload
enableQuickCheckLogs = true       // See terrain analysis
```

⚠️ **Warning**: These generate a LOT of logs. Only enable when investigating specific issues.

### Check Specific Coordinates

If a specific chunk is problematic (like -5, -1, -1):

```csharp
// In your code or Unity console
CoordinateDebugger.RunDiagnostic(new Vector3Int(-5, -1, -1), 16, 1.0f);
```

### Monitor Save System

```csharp
// Check current format
Debug.Log($"Save Format: {SaveSystem.GetSaveFormat()}");

// Check pending saves
Debug.Log($"Pending Saves: {SaveSystem.GetPendingSaveCount()}");
```

### Profile Frame Time

Watch for specific bottlenecks:
- **Chunk.Generate()**: Should complete in < 5ms per chunk
- **SaveSystem**: Should be async (not blocking main thread)
- **MarchingCubes**: Job system should parallelize

## Expected Performance Targets

### Before Optimization
- FPS during movement: 20-30
- Chunk load time: 10-50ms (JSON)
- Save file size: 50-200 KB per chunk
- Memory usage: High, no LOD

### After Optimization (What You Should See)
- ✅ FPS during movement: 50-60
- ✅ Chunk load time: 1-5ms (Binary)
- ✅ Save file size: 10-40 KB per chunk (compressed)
- ✅ Memory usage: 30-40% lower (with LOD)
- ✅ Format: BinaryCompressed
- ✅ Async I/O: Enabled
- ✅ Pending Saves: < 10
- ✅ Pending Ops: < 50
- ✅ Pool Available: > 20

## Testing Checklist

- [ ] Diagnostic overlay shows (press F3)
- [ ] Format = BinaryCompressed
- [ ] FPS = EXCELLENT or GOOD
- [ ] No yellow/red warnings
- [ ] Loaded chunks stable (not constantly changing)
- [ ] No flickering when moving
- [ ] Multiplayer: Client sees host's modifications
- [ ] Memory usage < 80%
- [ ] Pending Ops < 100

## If Nothing Seems to Work

1. **Verify SaveSystem initialized**:
   ```csharp
   // Add to World.Start() or Awake()
   SaveSystem.Initialize();
   Config?.InitializeSaveSystem();
   ```

2. **Check Unity Console** for errors

3. **Reduce Load Radius** temporarily:
   ```
   LoadRadius: 10 (test if this improves FPS)
   ```

4. **Disable LOD** to eliminate one variable:
   ```
   enableLOD: false
   ```

5. **Test with fresh world** (delete save folder)

6. **Share diagnostic output** - Screenshot F3 overlay when FPS drops

## Contact/Debug Info to Share

If asking for help, provide:
1. Screenshot of F3 diagnostic overlay during FPS drop
2. TerrainConfigs.asset settings
3. Unity version
4. Number of players
5. Any console errors



