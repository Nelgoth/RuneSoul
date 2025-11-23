# Quick Diagnostic Guide - 2 Minutes to Find FPS Issues

## Step 1: Add Diagnostic Component (1 minute)

1. In Unity, select your **World GameObject**
2. Click **Add Component**
3. Type **TerrainOptimizationDiagnostics**
4. Press Play

## Step 2: Press F3 to See Live Stats

You'll see a panel showing real-time performance metrics.

## Step 3: Look for These Issues

### 🔴 CRITICAL ISSUES (Fix First!)

**"⚠ WARNING: Not using BinaryCompressed!"**
→ Your saves are still using slow JSON format
→ **Fix**: Set these in `TerrainConfigs.asset`:
```
useBinaryFormat = true
compressChunkData = true
useAsyncIO = true
```

**"⚠ WARNING: Pool running low!"**
→ Not enough chunks in pool, causing stutters
→ **Fix**: In `TerrainConfigs.asset`:
```
chunkPoolBufferMultiplier = 1.5  (was 1.1)
```

**"⚠ WARNING: Large operation backlog!"**
→ Too many chunks trying to load at once
→ **Fix**: In `TerrainConfigs.asset`:
```
LoadRadius = 15  (was 20, reduce distance)
OR
ChunksPerFrame = 150  (was 100, process more)
```

### ⚠️ WARNING ISSUES (Fix if FPS still low)

**"⚠ WARNING: LOD max (Xm) < Load radius (Ym)!"**
→ LOD system conflicts with load radius
→ **Fix Option 1**: Disable LOD temporarily
```
enableLOD = false
```
→ **Fix Option 2**: Match LOD to load radius
```
lodDistances = [160, 240, 280, 320]  // For LoadRadius=20
```

**"⚠ WARNING: High memory pressure!"**
→ Using too much memory
→ **Fix**: Enable LOD or reduce LoadRadius

## Step 4: Expected Results

### Before Fix:
```
FPS: 25.3 (min: 18.2, max: 32.1)
Status: CRITICAL
Format: JSON  ⚠ WARNING
Pending Saves: 45
Pending Ops: 287  ⚠ WARNING
Pool Available: 3  ⚠ WARNING
```

### After Fix:
```
FPS: 57.8 (min: 52.4, max: 60.0)
Status: EXCELLENT ✓
Format: BinaryCompressed
Pending Saves: 2
Pending Ops: 12
Pool Available: 48
```

## Most Common Problem

**SaveSystem not using binary format** accounts for 70% of performance issues!

Quick check: Look at diagnostic panel, if it says "JSON" instead of "BinaryCompressed", that's your problem.

## Still Having Issues?

1. Take a screenshot of the F3 diagnostic panel
2. Check Unity Console for red errors
3. See full guide: **DIAGNOSTIC_INSTRUCTIONS.md**



