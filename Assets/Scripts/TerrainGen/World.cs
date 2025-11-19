//v1.0.1
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using NelsUtils;
using System;
using Unity.Netcode;
using ControllerAssets;
using System.IO;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class World : MonoBehaviour
{
    public struct PendingVoxelUpdate
    {
        public Vector3Int voxelPosition;
        public bool isAdding;
        public bool propagate;

        public PendingVoxelUpdate(Vector3Int voxelPosition, bool isAdding, bool propagate)
        {
            this.voxelPosition = voxelPosition;
            this.isAdding = isAdding;
            this.propagate = propagate;
        }
    }

    public struct PendingDensityPointUpdate
    {
        public Vector3Int pointPosition;
        public float newDensity;
        public Vector3 worldPosition; // Store world position for proper radius-based updates
        public bool forceBoundaryFalloff;

        public PendingDensityPointUpdate(Vector3Int pointPosition, float newDensity, Vector3 worldPosition, bool forceBoundaryFalloff = false)
        {
            this.pointPosition = pointPosition;
            this.newDensity = newDensity;
            this.worldPosition = worldPosition;
            this.forceBoundaryFalloff = forceBoundaryFalloff;
        }
    }

    private struct ChunkLoadRequest
    {
        public Vector3Int Coordinate;
        public float Distance;

        public ChunkLoadRequest(Vector3Int coord, float distance)
        {
            Coordinate = coord;
            Distance = distance;
        }
    }
    #region Constants and Static Properties
    public static World Instance { get; private set; }
    private Dictionary<Vector3Int, Chunk> activeChunks = new Dictionary<Vector3Int, Chunk>();
    private float lastUpdateTime = 0f;
    private bool justStarted = true;
    private bool ChunkModificationDiagnosticsEnabled => Config != null && Config.enableChunkModificationDiagnostics;
    #endregion

    #region Configuration Properties
    [SerializeField] private TerrainConfigs config;
    public TerrainConfigs Config => config;
    public int chunkSize => Config.chunkSize;
    public float voxelSize => Config.voxelSize;
    public Material VoxelMaterial => Config.VoxelMaterial;
    public int noiseSeed => Config.noiseSeed;
    public float maxHeight => Config.maxHeight;
    public float noiseScale => Config.noiseScale;
    public float frequency => Config.frequency;
    public float surfaceLevel => Config.surfaceLevel;
    private bool ChunkLifecycleLogsEnabled => config != null && config.enableChunkLifecycleLogs;
    
    [Header("Debug Tracing")]
    [Tooltip("Automatically enable tracing for all chunks affected by mining operations")]
    public bool autoTraceMiningOperations = false;
    
    /// <summary>
    /// Enable detailed tracing for a specific chunk. All operations on this chunk will be logged.
    /// </summary>
    public void EnableChunkTracing(Vector3Int chunkCoord)
    {
        if (tracedChunks == null)
        {
            tracedChunks = new HashSet<Vector3Int>();
        }
        if (chunkTraceLogs == null)
        {
            chunkTraceLogs = new Dictionary<Vector3Int, List<string>>();
        }
        
        if (!tracedChunks.Contains(chunkCoord))
        {
            tracedChunks.Add(chunkCoord);
            chunkTraceLogs[chunkCoord] = new List<string>();
            Debug.Log($"[CHUNK_TRACE] Enabled tracing for chunk {chunkCoord}");
        }
    }
    
    /// <summary>
    /// Disable tracing for a specific chunk.
    /// </summary>
    public void DisableChunkTracing(Vector3Int chunkCoord)
    {
        if (tracedChunks != null)
        {
            tracedChunks.Remove(chunkCoord);
        }
        if (chunkTraceLogs != null)
        {
            chunkTraceLogs.Remove(chunkCoord);
        }
    }
    
    /// <summary>
    /// Get all trace logs for a specific chunk.
    /// </summary>
    public List<string> GetChunkTraceLogs(Vector3Int chunkCoord)
    {
        if (chunkTraceLogs != null && chunkTraceLogs.TryGetValue(chunkCoord, out var logs))
        {
            return new List<string>(logs);
        }
        return new List<string>();
    }
    
    /// <summary>
    /// Log a trace message for a specific chunk if tracing is enabled.
    /// </summary>
    private void LogChunkTrace(Vector3Int chunkCoord, string message)
    {
        try
        {
            if (tracedChunks == null || !tracedChunks.Contains(chunkCoord))
            {
                return;
            }
            
            string traceMessage = $"[CHUNK_TRACE:{chunkCoord}] {message}";
            Debug.Log(traceMessage);
            
            if (chunkTraceLogs != null && chunkTraceLogs.TryGetValue(chunkCoord, out var logs) && logs != null)
            {
                logs.Add($"[{Time.time:F3}] {message}");
                if (logs.Count > MAX_TRACE_LOG_ENTRIES)
                {
                    logs.RemoveAt(0); // Remove oldest entry
                }
            }
        }
        catch (System.Exception e)
        {
            // Silently fail - don't let tracing errors break the main flow
            Debug.LogWarning($"[LogChunkTrace] Error logging trace for chunk {chunkCoord}: {e.Message}");
        }
    }
    
    /// <summary>
    /// Check if a chunk is being traced.
    /// </summary>
    public bool IsChunkTraced(Vector3Int chunkCoord)
    {
        return tracedChunks != null && tracedChunks.Contains(chunkCoord);
    }
    
    private bool ShouldLogChunkDiagnostics(Vector3Int chunkCoord)
    {
        return ChunkModificationDiagnosticsEnabled || IsChunkTraced(chunkCoord);
    }
    
    /// <summary>
    /// Enable tracing for all chunks affected by a mining operation at a specific world position.
    /// This helps identify problematic chunks when mining.
    /// </summary>
    public void EnableTracingForMiningOperation(Vector3 worldPos)
    {
        float radius = voxelSize * (Config.densityInfluenceRadius + 1f);
        var affectedChunks = GetAffectedChunks(worldPos, radius);
        
        Debug.Log($"[CHUNK_TRACE] Enabling tracing for {affectedChunks.Count} chunks affected by mining at {worldPos}");
        
        foreach (var chunkCoord in affectedChunks)
        {
            EnableChunkTracing(chunkCoord);
        }
    }
    
    /// <summary>
    /// Enable tracing for a specific chunk and all its immediate neighbors (3x3x3 grid).
    /// Useful for debugging chunks that refuse to load.
    /// </summary>
    public void EnableTracingForChunkAndNeighbors(Vector3Int chunkCoord)
    {
        Debug.Log($"[CHUNK_TRACE] Enabling tracing for chunk {chunkCoord} and all neighbors");
        
        // Enable tracing for the center chunk
        EnableChunkTracing(chunkCoord);
        
        // Enable tracing for all neighbors (3x3x3 grid centered on the chunk)
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue; // Skip center (already enabled)
            
            Vector3Int neighborCoord = chunkCoord + new Vector3Int(dx, dy, dz);
            EnableChunkTracing(neighborCoord);
        }
        
        Debug.Log($"[CHUNK_TRACE] Enabled tracing for chunk {chunkCoord} and 26 neighbors (27 total chunks)");
    }
    
    /// <summary>
    /// Print comprehensive diagnostics for a specific chunk coordinate.
    /// This helps identify why a chunk might not be loading or applying updates.
    /// </summary>
    public void PrintChunkDiagnostics(Vector3Int chunkCoord)
    {
        if (ChunkStateManager.Instance == null)
        {
            Debug.LogError($"[CHUNK_DIAGNOSTICS] ChunkStateManager.Instance is null - cannot print diagnostics");
            return;
        }
        
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
        bool isQuarantined = ChunkStateManager.Instance.QuarantinedChunks != null && ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord);
        bool isLoaded = chunks != null && chunks.ContainsKey(chunkCoord);
        bool hasPendingVoxelUpdates = pendingVoxelUpdates != null && pendingVoxelUpdates.ContainsKey(chunkCoord);
        bool hasPendingDensityUpdates = pendingDensityPointUpdates != null && pendingDensityPointUpdates.ContainsKey(chunkCoord);
        bool isMarkedForMod = modifiedSolidChunks != null && modifiedSolidChunks.Contains(chunkCoord);
        bool isForcedForLoad = IsChunkForcedForLoad(chunkCoord);
        bool hasQueuedMining = miningOperationQueues != null && miningOperationQueues.ContainsKey(chunkCoord);
        bool isCurrentlyProcessing = currentlyProcessingChunks != null && currentlyProcessingChunks.Contains(chunkCoord);
        
        TerrainAnalysisData analysis = null;
        TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out analysis);
        
        Chunk chunk = null;
        ChunkData chunkData = null;
        if (isLoaded)
        {
            chunks.TryGetValue(chunkCoord, out chunk);
            if (chunk != null)
            {
                chunkData = chunk.GetChunkData();
            }
        }
        
        Debug.Log($"[CHUNK_DIAGNOSTICS] Comprehensive diagnostics for chunk {chunkCoord}:\n" +
            $"  === STATE ===\n" +
            $"  Status: {state.Status}\n" +
            $"  Flags: {state.Flags}\n" +
            $"  Quarantined: {isQuarantined}\n" +
            $"  Loaded: {isLoaded}\n" +
            $"  Currently Processing: {isCurrentlyProcessing}\n" +
            $"  === PENDING OPERATIONS ===\n" +
            $"  HasPendingVoxelUpdates: {hasPendingVoxelUpdates} (Count: {(hasPendingVoxelUpdates ? pendingVoxelUpdates[chunkCoord].Count : 0)})\n" +
            $"  HasPendingDensityUpdates: {hasPendingDensityUpdates} (Count: {(hasPendingDensityUpdates ? pendingDensityPointUpdates[chunkCoord].Count : 0)})\n" +
            $"  HasQueuedMining: {hasQueuedMining} (QueueSize: {(hasQueuedMining ? miningOperationQueues[chunkCoord].Count : 0)})\n" +
            $"  === TERRAIN ANALYSIS ===\n" +
            $"  IsEmpty: {(analysis != null ? analysis.IsEmpty.ToString() : "N/A (not in cache)")}\n" +
            $"  IsSolid: {(analysis != null ? analysis.IsSolid.ToString() : "N/A (not in cache)")}\n" +
            $"  WasModified: {(analysis != null ? analysis.WasModified.ToString() : "N/A (not in cache)")}\n" +
            $"  === CHUNK DATA ===\n" +
            $"  Chunk Object: {(chunk != null ? "Exists" : "NULL")}\n" +
            $"  ChunkData: {(chunkData != null ? "Exists" : "NULL")}\n" +
            $"  IsMarkedForModification: {isMarkedForMod}\n" +
            $"  ForcedForLoad: {isForcedForLoad}\n" +
            $"  === CHUNK DATA DETAILS ===\n" +
            $"  {(chunkData != null ? $"IsEmptyChunk: {chunkData.IsEmptyChunk}, IsSolidChunk: {chunkData.IsSolidChunk}, HasModifiedData: {chunkData.HasModifiedData}" : "N/A (chunk not loaded)")}\n" +
            $"  === OPERATIONS QUEUE ===\n" +
            $"  {(operationsQueue != null ? $"HasPendingLoad: {operationsQueue.HasPendingLoadOperation(chunkCoord)}" : "OperationsQueue is null")}");
        
        // Print pending density update details if any
        if (hasPendingDensityUpdates && pendingDensityPointUpdates != null && pendingDensityPointUpdates.ContainsKey(chunkCoord))
        {
            var updates = pendingDensityPointUpdates[chunkCoord];
            Debug.Log($"[CHUNK_DIAGNOSTICS] Pending density updates for {chunkCoord} (first 5):");
            for (int i = 0; i < Mathf.Min(5, updates.Count); i++)
            {
                var update = updates[i];
                Debug.Log($"  [{i}] WorldPos: {update.worldPosition}, DensityPos: {update.pointPosition}, NewDensity: {update.newDensity}");
            }
        }
        
        // Print neighbor states
        Debug.Log($"[CHUNK_DIAGNOSTICS] Neighbor states for {chunkCoord}:");
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            
            Vector3Int neighborCoord = chunkCoord + new Vector3Int(dx, dy, dz);
            var neighborState = ChunkStateManager.Instance.GetChunkState(neighborCoord);
            bool neighborLoaded = chunks.ContainsKey(neighborCoord);
            bool neighborHasPending = HasPendingUpdates(neighborCoord);
            
            Debug.Log($"  Neighbor {neighborCoord}: State={neighborState.Status}, Loaded={neighborLoaded}, HasPending={neighborHasPending}");
        }
    }
    
    /// <summary>
    /// Helper method to identify and trace a chunk from a world position.
    /// Useful when you find a problematic chunk while mining - just provide the world position.
    /// </summary>
    public void IdentifyAndTraceChunkFromWorldPos(Vector3 worldPos)
    {
        Vector3Int chunkCoord = Coord.WorldToChunkCoord(worldPos, chunkSize, voxelSize);
        Debug.Log($"[CHUNK_IDENTIFY] World position {worldPos} corresponds to chunk {chunkCoord}");
        Debug.Log($"[CHUNK_IDENTIFY] Enabling tracing and diagnostics for chunk {chunkCoord} and neighbors...");
        
        // Enable tracing for this chunk and neighbors
        EnableTracingForChunkAndNeighbors(chunkCoord);
        
        // Print diagnostics immediately
        PrintChunkDiagnostics(chunkCoord);
        
        Debug.Log($"[CHUNK_IDENTIFY] To view trace summary later, call: World.Instance.PrintChunkTraceSummary(new Vector3Int({chunkCoord.x}, {chunkCoord.y}, {chunkCoord.z}))");
    }
    
    /// <summary>
    /// Get chunk coordinates from a world position. Useful for identifying unloaded chunks.
    /// </summary>
    public Vector3Int GetChunkCoordFromWorldPos(Vector3 worldPos)
    {
        return Coord.WorldToChunkCoord(worldPos, chunkSize, voxelSize);
    }
    
    /// <summary>
    /// Diagnose a specific chunk coordinate - check if it's loaded, its state, pending updates, etc.
    /// </summary>
    public void DiagnoseChunk(Vector3Int chunkCoord)
    {
        Debug.Log($"[CHUNK_DIAGNOSIS] ===== DIAGNOSING CHUNK {chunkCoord} =====");
        
        bool isLoaded = chunks.ContainsKey(chunkCoord);
        var state = ChunkStateManager.Instance?.GetChunkState(chunkCoord);
        bool hasPendingUpdates = HasPendingUpdates(chunkCoord);
        bool isQuarantined = ChunkStateManager.Instance?.QuarantinedChunks.Contains(chunkCoord) ?? false;
        bool isMarkedForMod = modifiedSolidChunks.Contains(chunkCoord);
        bool isForcedForLoad = IsChunkForcedForLoad(chunkCoord);
        
        Debug.Log($"[CHUNK_DIAGNOSIS] Chunk {chunkCoord}:");
        Debug.Log($"  - Loaded: {isLoaded}");
        Debug.Log($"  - State: {state?.Status}");
        Debug.Log($"  - Quarantined: {isQuarantined}");
        Debug.Log($"  - HasPendingUpdates: {hasPendingUpdates}");
        Debug.Log($"  - IsMarkedForMod: {isMarkedForMod}");
        Debug.Log($"  - ForcedForLoad: {isForcedForLoad}");
        
        if (hasPendingUpdates)
        {
            Debug.Log($"  - VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}");
            Debug.Log($"  - DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}");
            if (pendingDensityPointUpdates.ContainsKey(chunkCoord))
            {
                Debug.Log($"  - DensityUpdateCount: {pendingDensityPointUpdates[chunkCoord].Count}");
            }
        }
        
        if (isLoaded && chunks.TryGetValue(chunkCoord, out Chunk chunk))
        {
            var chunkData = chunk.GetChunkData();
            Debug.Log($"  - ChunkData: {(chunkData != null ? "Exists" : "NULL")}");
            if (chunkData != null)
            {
                Debug.Log($"  - IsEmptyChunk: {chunkData.IsEmptyChunk}");
                Debug.Log($"  - IsSolidChunk: {chunkData.IsSolidChunk}");
                Debug.Log($"  - HasModifiedData: {chunkData.HasModifiedData}");
            }
            Debug.Log($"  - IsMeshUpdateQueued: {chunk.isMeshUpdateQueued}");
            Debug.Log($"  - GenerationCoroutine: {(chunk.generationCoroutine != null ? "Running" : "None")}");
        }
        
        // Check TerrainAnalysisCache
        if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis != null)
        {
            Debug.Log($"  - Cache: IsSolid={analysis.IsSolid}, IsEmpty={analysis.IsEmpty}, WasModified={analysis.WasModified}");
        }
        else
        {
            Debug.Log($"  - Cache: Not in cache");
        }
        
        // CRITICAL: Check if chunk has saved data that might indicate it was previously modified
        // Construct save path manually (matching SaveSystem.GetChunkFilePath logic)
        try
        {
            if (WorldSaveManager.Instance != null && !string.IsNullOrEmpty(WorldSaveManager.Instance.WorldSaveFolder))
            {
                string folderPath = WorldSaveManager.Instance.WorldSaveFolder + "/Chunks";
                string fileName = $"chunk_{chunkCoord.x}_{chunkCoord.y}_{chunkCoord.z}.json";
                string savePath = folderPath + "/" + fileName;
                bool saveFileExists = System.IO.File.Exists(savePath);
                Debug.Log($"  - SaveFileExists: {saveFileExists}");
                if (saveFileExists)
                {
                    Debug.LogWarning($"  - WARNING: Chunk {chunkCoord} has saved data but may be marked as Solid - this could prevent loading saved modifications!");
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"  - Could not check save file: {e.Message}");
        }
        
        // Check if chunk is tracked for stuck detection
        if (chunkLoadingStartTime.ContainsKey(chunkCoord))
        {
            float timeStuck = Time.time - chunkLoadingStartTime[chunkCoord];
            Debug.LogWarning($"  - STUCK IN LOADING: {timeStuck:F2} seconds");
        }
        
        // Check loadValidationCache
        if (loadValidationCache.ContainsKey(chunkCoord))
        {
            float cacheTime = loadValidationCache[chunkCoord];
            float timeSinceCache = Time.time - cacheTime;
            Debug.Log($"  - LoadValidationCache: Cached {timeSinceCache:F2}s ago (may prevent re-evaluation)");
        }
        
        // Check if chunk is in chunksBeingValidated (would prevent ShouldLoadChunk from running)
        if (chunksBeingValidated.Contains(chunkCoord))
        {
            Debug.LogWarning($"  - WARNING: Chunk {chunkCoord} is currently being validated (chunksBeingValidated) - this prevents ShouldLoadChunk from running!");
        }
        
        // Enable tracing for this chunk
        EnableTracingForChunkAndNeighbors(chunkCoord);
        PrintChunkDiagnostics(chunkCoord);
        
        Debug.Log($"[CHUNK_DIAGNOSIS] ===== END DIAGNOSIS FOR {chunkCoord} =====");
    }
    
    /// <summary>
    /// Analyze neighbors of given chunks to identify missing chunks. Useful for finding holes.
    /// Call this with the chunks around a hole to find which chunk is missing.
    /// Example: AnalyzeMissingChunksFromNeighbors(new List<Vector3Int> { new Vector3Int(6,2,-1), new Vector3Int(6,2,0), ... })
    /// </summary>
    public void AnalyzeMissingChunksFromNeighbors(List<Vector3Int> neighborChunks)
    {
        Debug.Log($"[MISSING_CHUNK_ANALYSIS] Analyzing {neighborChunks.Count} neighbor chunks to find missing chunk...");
        
        // Collect all potential neighbors
        HashSet<Vector3Int> allNeighbors = new HashSet<Vector3Int>();
        
        foreach (var chunkCoord in neighborChunks)
        {
            // Add all 26 neighbors (3x3x3 cube minus center)
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                if (x == 0 && y == 0 && z == 0) continue; // Skip self
                Vector3Int neighbor = chunkCoord + new Vector3Int(x, y, z);
                allNeighbors.Add(neighbor);
            }
        }
        
        // Find neighbors that are NOT in the provided list and NOT loaded
        List<Vector3Int> missingChunks = new List<Vector3Int>();
        List<Vector3Int> loadedButNotInList = new List<Vector3Int>();
        
        // Also check which chunks from the provided list are NOT loaded (these are the actual missing chunks!)
        List<Vector3Int> providedButNotLoaded = new List<Vector3Int>();
        
        foreach (var providedChunk in neighborChunks)
        {
            if (!chunks.ContainsKey(providedChunk))
            {
                providedButNotLoaded.Add(providedChunk);
            }
        }
        
        foreach (var potentialChunk in allNeighbors)
        {
            bool isInProvidedList = neighborChunks.Contains(potentialChunk);
            bool isLoaded = chunks.ContainsKey(potentialChunk);
            
            if (!isInProvidedList)
            {
                if (!isLoaded)
                {
                    missingChunks.Add(potentialChunk);
                }
                else
                {
                    loadedButNotInList.Add(potentialChunk);
                }
            }
        }
        
        // CRITICAL: Report chunks from the provided list that are NOT loaded - these are the actual missing chunks!
        if (providedButNotLoaded.Count > 0)
        {
            Debug.LogError($"[MISSING_CHUNK_ANALYSIS] CRITICAL: Found {providedButNotLoaded.Count} chunks from YOUR LIST that are NOT LOADED:");
            foreach (var missing in providedButNotLoaded)
            {
                var state = ChunkStateManager.Instance?.GetChunkState(missing);
                    bool hasPendingUpdates = HasPendingUpdates(missing);
                    bool isForcedForLoad = IsChunkForcedForLoad(missing);
                bool isQuarantined = ChunkStateManager.Instance?.QuarantinedChunks.Contains(missing) ?? false;
                
                Debug.LogError($"[MISSING_CHUNK_ANALYSIS] MISSING CHUNK FROM YOUR LIST: {missing} - " +
                        $"State: {state?.Status}, HasPendingUpdates: {hasPendingUpdates}, ForcedForLoad: {isForcedForLoad}, Quarantined: {isQuarantined}");
                
                // Enable tracing for missing chunks
                EnableTracingForChunkAndNeighbors(missing);
                
                // Print diagnostics
                PrintChunkDiagnostics(missing);
            }
        }
        
        Debug.Log($"[MISSING_CHUNK_ANALYSIS] Found {missingChunks.Count} potentially missing chunks:");
        foreach (var missing in missingChunks)
        {
            var state = ChunkStateManager.Instance?.GetChunkState(missing);
                bool hasPendingUpdates = HasPendingUpdates(missing);
                bool isForcedForLoad = IsChunkForcedForLoad(missing);
            bool isQuarantined = ChunkStateManager.Instance?.QuarantinedChunks.Contains(missing) ?? false;
            
            Debug.LogWarning($"[MISSING_CHUNK_ANALYSIS] MISSING CHUNK: {missing} - " +
                    $"State: {state?.Status}, HasPendingUpdates: {hasPendingUpdates}, ForcedForLoad: {isForcedForLoad}, Quarantined: {isQuarantined}");
            
            // Enable tracing for missing chunks
            EnableTracingForChunkAndNeighbors(missing);
            
            // Print diagnostics
            PrintChunkDiagnostics(missing);
        }
        
        if (loadedButNotInList.Count > 0)
        {
            Debug.Log($"[MISSING_CHUNK_ANALYSIS] Found {loadedButNotInList.Count} loaded chunks not in provided list (these are neighbors):");
            foreach (var loaded in loadedButNotInList)
            {
                Debug.Log($"[MISSING_CHUNK_ANALYSIS] Loaded neighbor: {loaded}");
            }
        }
        
        // Also check which of the provided chunks are actually loaded
        Debug.Log($"[MISSING_CHUNK_ANALYSIS] Status of provided chunks:");
        foreach (var chunkCoord in neighborChunks)
        {
            bool isLoaded = chunks.ContainsKey(chunkCoord);
            var state = ChunkStateManager.Instance?.GetChunkState(chunkCoord);
                bool hasPendingUpdates = HasPendingUpdates(chunkCoord);
                bool isForcedForLoad = IsChunkForcedForLoad(chunkCoord);
            
            Debug.Log($"[MISSING_CHUNK_ANALYSIS] Chunk {chunkCoord} - " +
                    $"Loaded: {isLoaded}, State: {state?.Status}, HasPendingUpdates: {hasPendingUpdates}, ForcedForLoad: {isForcedForLoad}");
        }
    }
    
    /// <summary>
    /// Print a summary of a traced chunk's lifecycle for debugging.
    /// </summary>
    public void PrintChunkTraceSummary(Vector3Int chunkCoord)
    {
        if (tracedChunks == null || !tracedChunks.Contains(chunkCoord))
        {
            Debug.LogWarning($"[CHUNK_TRACE] Chunk {chunkCoord} is not being traced. Enable tracing first.");
            return;
        }
        
        var logs = GetChunkTraceLogs(chunkCoord);
        
        if (ChunkStateManager.Instance == null)
        {
            Debug.LogError($"[CHUNK_TRACE] ChunkStateManager.Instance is null - cannot print summary");
            return;
        }
        
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
        bool isQuarantined = ChunkStateManager.Instance.QuarantinedChunks != null && ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord);
        bool hasPendingUpdates = HasPendingUpdates(chunkCoord);
        bool isLoaded = chunks != null && chunks.ContainsKey(chunkCoord);
        bool isMarkedForMod = modifiedSolidChunks != null && modifiedSolidChunks.Contains(chunkCoord);
        
        Debug.Log($"[CHUNK_TRACE] Summary for chunk {chunkCoord}:\n" +
            $"  State: {state.Status}\n" +
            $"  Quarantined: {isQuarantined}\n" +
            $"  Loaded: {isLoaded}\n" +
            $"  HasPendingUpdates: {hasPendingUpdates}\n" +
            $"  IsMarkedForMod: {isMarkedForMod}\n" +
            $"  VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}\n" +
            $"  DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}\n" +
            $"  TraceLogEntries: {logs.Count}\n" +
            $"  Last 10 log entries:");
        
        int startIndex = Mathf.Max(0, logs.Count - 10);
        for (int i = startIndex; i < logs.Count; i++)
        {
            Debug.Log($"    {logs[i]}");
        }
    }
    #endregion

    #region Chunk Loading Configuration
    public int loadRadius => Config.LoadRadius;
    public int unloadRadius => Config.UnloadRadius;
    public int verticalLoadRadius => Config.VerticalLoadRadius;
    public int verticalUnloadRadius => Config.VerticalUnloadRadius;
    public int chunksPerFrame => MeshDataPool.Instance.GetDynamicChunksPerFrame();
    public bool IsInitialLoadInProgress => initialLoadInProgress;
    public bool IsInitialLoadUnloadingEmptyChunks => initialLoadInProgress && initialLoadStage == InitialLoadStage.UnloadingEmptyChunks;
    public int InitialEmptyChunksPending => initialLoadEmptyPendingUnload.Count;
    public int InitialEmptyChunksProcessed => initialLoadEmptyProcessed;
    public int InitialEmptyChunksTotal => Mathf.Max(initialLoadEmptyTracked.Count, initialLoadEmptyProcessed + initialLoadEmptyPendingUnload.Count);
    public bool IsInitialLoadComplete => !initialLoadInProgress;
    public float InitialLoadProgress => initialLoadProgress;
    public int InitialLoadChunkBudget => Config != null ? Config.GetInitialLoadChunkBudget() : 256;
    #endregion

    #region Private Fields
    private Dictionary<Vector3Int, Chunk> chunks = new Dictionary<Vector3Int, Chunk>();
    public int ActiveChunkCount => chunks.Count;
    private ThirdPersonController playerController;
    private Vector3 playerPosition;
    private Vector3Int lastPlayerChunkCoordinates;
    private bool playerMovementLocked = false;
    private ChunkOperationsQueue operationsQueue;
    private readonly object updateLock = new object();
    #endregion

    #region Update Queue Structures
    private HashSet<Chunk> chunksNeedingMeshUpdate = new HashSet<Chunk>();
    public Dictionary<Vector3Int, List<PendingVoxelUpdate>> pendingVoxelUpdates = new Dictionary<Vector3Int, List<PendingVoxelUpdate>>();
    public Dictionary<Vector3Int, List<PendingDensityPointUpdate>> pendingDensityPointUpdates = new Dictionary<Vector3Int, List<PendingDensityPointUpdate>>();
    private Dictionary<Vector3Int, Chunk> allChunks = new Dictionary<Vector3Int, Chunk>();
    private HashSet<Vector3Int> currentlyProcessingChunks = new HashSet<Vector3Int>();
    private Dictionary<Vector3Int, Queue<Vector3Int>> miningOperationQueues = new Dictionary<Vector3Int, Queue<Vector3Int>>();
    private HashSet<Vector3Int> chunksWithQueuedMining = new HashSet<Vector3Int>();
    #endregion

    #region Reusable Lists
    private List<Vector3Int> chunksToLoad = new List<Vector3Int>();
    private List<Vector3Int> pendingVoxelUpdatesKeys = new List<Vector3Int>();
    private HashSet<Vector3Int> chunksWithPendingNeighborUpdates = new HashSet<Vector3Int>();
    #endregion

    private Dictionary<Vector3Int, ChunkData> chunkDataMap = new Dictionary<Vector3Int, ChunkData>();
    private HashSet<Vector3Int> chunksInQuarantine = new HashSet<Vector3Int>();
    private Dictionary<Vector3Int, int> chunkLoadAttempts = new Dictionary<Vector3Int, int>();

    private HashSet<Vector3Int> chunksBeingValidated = new HashSet<Vector3Int>();
    private Dictionary<Vector3Int, float> loadValidationCache = new Dictionary<Vector3Int, float>();
    private Dictionary<Vector3Int, float> chunkLoadingStartTime = new Dictionary<Vector3Int, float>();
    private const float STUCK_CHUNK_TIMEOUT = 10f; // 10 seconds - if a chunk is in Loading state for this long, consider it stuck
    private const float VALIDATION_CACHE_TIME = 0.1f; // Cache results for 100ms
    private const float NearbyChunkAccessRefreshInterval = 2f;
    private const int NearbyChunkHorizontalRadius = 5;
    private const int NearbyChunkVerticalRadius = 3;
    private const float ImmediateLoadDistanceThreshold = 5f;

    private HashSet<Vector3Int> modifiedSolidChunks = new HashSet<Vector3Int>();
    private HashSet<Vector3Int> forcedBoundaryChunks = new HashSet<Vector3Int>();
    private float lastNearbyAccessRefreshTime = -10f;
    private readonly List<Vector3Int> accessTouchCenters = new List<Vector3Int>();
    private Dictionary<ulong, Vector3> activePlayerPositions = new Dictionary<ulong, Vector3>();
    private Dictionary<ulong, Vector3Int> playerChunkCoordinates = new Dictionary<ulong, Vector3Int>();
    private HashSet<Vector3Int> activeChunkCoords = new HashSet<Vector3Int>();
    private float lastGlobalChunkUpdateTime = 0f;
    
    // Enhanced logging: Track specific chunks for detailed debugging
    private HashSet<Vector3Int> tracedChunks = new HashSet<Vector3Int>();
    private Dictionary<Vector3Int, List<string>> chunkTraceLogs = new Dictionary<Vector3Int, List<string>>();
    private const int MAX_TRACE_LOG_ENTRIES = 1000; // Limit trace log size per chunk
    [SerializeField] public GameObject chunkPrefab;
    [Header("Initial Load")]
    private bool initialLoadInProgress = true;
    private float initialLoadProgress = 0f;
    private const int InitialLoadTerrainCacheFlushBatch = 8192;
    private readonly HashSet<Vector3Int> initialLoadTargets = new HashSet<Vector3Int>();
    private readonly HashSet<Vector3Int> initialLoadPending = new HashSet<Vector3Int>();
    private readonly Queue<Vector3Int> initialLoadEmptyUnloadQueue = new Queue<Vector3Int>();
    private readonly HashSet<Vector3Int> initialLoadEmptyQueued = new HashSet<Vector3Int>();
    private readonly HashSet<Vector3Int> initialLoadEmptyTracked = new HashSet<Vector3Int>();
    private readonly HashSet<Vector3Int> initialLoadEmptyPendingUnload = new HashSet<Vector3Int>();
    private int initialLoadEmptyTotal = 0;
    private int initialLoadEmptyProcessed = 0;
    private int initialLoadTerrainTotal = 0;
    private int initialLoadTerrainProcessed = 0;
    private enum InitialLoadStage { LoadingChunks, ProcessingTerrainCache, UnloadingEmptyChunks, Complete }
    private InitialLoadStage initialLoadStage = InitialLoadStage.LoadingChunks;
    private float initialLoadStartTime = -1f;
    private bool initialLoadCompletionBroadcasted = false;
    private class ChunkUnloadCandidate
    {
        public Vector3Int chunkCoord;
        public float priority;
        public float distanceToPlayers;
        public float ageFactor;
        
        public ChunkUnloadCandidate(Vector3Int coord, float priority, float distance, float age)
        {
            this.chunkCoord = coord;
            this.priority = priority;
            this.distanceToPlayers = distance;
            this.ageFactor = age;
        }
    }
    #region Unity Lifecycle
    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            chunks = new Dictionary<Vector3Int, Chunk>();
            if (config != null)
            {
                TerrainAnalysisCache.ApplyLoggingFromConfig(config);
            }
            // Important: Make sure we're spawned before any chunks try to parent to us
            var netObj = GetComponent<NetworkObject>();
            if (netObj != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                if (!netObj.IsSpawned)
                {
                    netObj.Spawn();
                }
            }
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void OnValidate()
    {
        if (config != null)
        {
            TerrainAnalysisCache.ApplyLoggingFromConfig(config);
        }
    }

   private void Start()
    {
        StartCoroutine(InitializeWorldAfterPlayerReady());
    }

    private void Update()
    {
        UpdateWorldState();
        if (Time.frameCount % 1800 == 0) // Every ~30 seconds at 60fps
        {
            TerrainAnalysisCache.CleanupOldAnalysis();
        }
        ProcessQuarantinedChunks();
        DetectAndRecoverStuckChunks();
        TerrainAnalysisCache.Update();
        CleanupValidationCache();
        ProcessMiningQueues();
    }

    private void ProcessMiningQueues()
    {
        lock (updateLock)
        {
            // Process one mining operation per chunk per frame
            var chunksToProcess = new List<Vector3Int>(miningOperationQueues.Keys);
            
            foreach (var chunkCoord in chunksToProcess)
            {
                // Skip if already processing this chunk
                if (currentlyProcessingChunks.Contains(chunkCoord))
                {
                    continue;
                }

                // Check if chunk is loaded and ready
                if (!chunks.ContainsKey(chunkCoord))
                {
                    var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                    if (state.Status == ChunkConfigurations.ChunkStatus.None || 
                        state.Status == ChunkConfigurations.ChunkStatus.Unloaded)
                    {
                        RequestChunkLoad(chunkCoord);
                    }
                    continue;
                }

                var state2 = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                if (state2.Status != ChunkConfigurations.ChunkStatus.Loaded && 
                    state2.Status != ChunkConfigurations.ChunkStatus.Modified)
                {
                    continue;
                }

                // Process one operation from this chunk's queue
                if (miningOperationQueues[chunkCoord].Count > 0)
                {
                    var voxelPos = miningOperationQueues[chunkCoord].Dequeue();
                    
                    // Process this mining operation
                    HandleVoxelDestruction(chunkCoord, voxelPos);
                    
                    // Clean up empty queue
                    if (miningOperationQueues[chunkCoord].Count == 0)
                    {
                        miningOperationQueues.Remove(chunkCoord);
                        chunksWithQueuedMining.Remove(chunkCoord);
                    }
                }
            }
        }
    }
    #endregion

    #region Initialization
    private IEnumerator InitializeWorldAfterPlayerReady()
    {
        Debug.Log("Starting World initialization...");
        
        // First wait for required managers with timeout
        float managerWaitTime = 0f;
        float maxManagerWaitTime = 10f;  // 10 seconds timeout
        
        // Wait for all required managers to be available
        while (managerWaitTime < maxManagerWaitTime)
        {
            // Check if all managers are ready
            bool managersReady = 
                ChunkStateManager.Instance != null && 
                ChunkPoolManager.Instance != null && 
                ChunkOperationsQueue.Instance != null &&
                MeshDataPool.Instance != null;
                
            if (managersReady)
            {
                break;
            }
            
            // Log current status and wait
            Debug.Log($"Waiting for managers... StateManager: {ChunkStateManager.Instance != null}, " +
                    $"PoolManager: {ChunkPoolManager.Instance != null}, " +
                    $"OperationsQueue: {ChunkOperationsQueue.Instance != null}, " +
                    $"MeshPool: {MeshDataPool.Instance != null}");
            
            managerWaitTime += 0.2f;
            yield return new WaitForSeconds(0.2f);
        }
        
        // Check if we timed out waiting for managers
        bool managersMissing = 
            ChunkStateManager.Instance == null || 
            ChunkPoolManager.Instance == null || 
            ChunkOperationsQueue.Instance == null ||
            MeshDataPool.Instance == null;
            
        if (managersMissing)
        {
            Debug.LogError("Timed out waiting for required managers! World initialization failed.");
            yield break;
        }

        // Wait for player with better error handling
        float maxPlayerWaitTime = 20f;  // 20 seconds total timeout
        int maxRetries = 5;
        int currentRetry = 0;

        while (currentRetry < maxRetries)
        {
            float timeWaited = 0f;
            ThirdPersonController player = null;

            // Try to find player with timeout
            while (timeWaited < maxPlayerWaitTime / maxRetries && player == null)
            {
                // Look for any player objects including clones
                ThirdPersonController[] allPlayers = null;
                
                // Safely find players
                bool findFailed = false;
                
                try
                {
                    allPlayers = FindObjectsByType<ThirdPersonController>(FindObjectsSortMode.None);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error finding players: {e.Message}");
                    findFailed = true;
                }
                
                // If find operation succeeded, check for valid players
                if (!findFailed && allPlayers != null && allPlayers.Length > 0)
                {
                    foreach (var p in allPlayers)
                    {
                        if (p != null && p.gameObject.activeInHierarchy)
                        {
                            Debug.Log($"Found player: {p.gameObject.name} at position {p.transform.position}");
                            player = p;
                            break;
                        }
                    }
                }

                // If player not found yet, wait and try again
                if (player == null)
                {
                    timeWaited += 0.5f;
                    yield return new WaitForSeconds(0.5f);
                }
            }

            // If player found, proceed with initialization
            if (player != null)
            {
                playerController = player;
                playerPosition = player.transform.position;
                
                Debug.Log($"Player found at position {playerPosition}");
                lastPlayerChunkCoordinates = Coord.WorldToChunkCoord(playerPosition, chunkSize, voxelSize);
                Debug.Log($"Initial chunk coordinates: {lastPlayerChunkCoordinates}");
                
                // Initialize the world
                bool initFailed = false;
                
                try
                {
                    InitializeWorld();
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error during world initialization: {e.Message}\n{e.StackTrace}");
                    initFailed = true;
                }
                if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized && 
                    Time.frameCount % 300 == 0) // Check every ~5 seconds
                {
                    // This ensures TerrainAnalysisCache has loaded the correct world data
                    TerrainAnalysisCache.Update();
                }
                // If initialization succeeded, we're done
                if (!initFailed)
                {
                    Debug.Log("World initialization successful");
                    yield break;
                }
                
                // Otherwise retry
                currentRetry++;
                yield return new WaitForSeconds(1f);
            }
            else
            {
                currentRetry++;
                Debug.LogWarning($"Player not found, retry {currentRetry}/{maxRetries}");
                yield return new WaitForSeconds(1f);
            }
        }

        Debug.LogError("Failed to find player after all retries! World initialization failed.");
    }

    private void ApplyWorldSeedFromMetadata()
    {
        if (config == null)
        {
            Debug.LogWarning("[World] Cannot apply world seed - TerrainConfigs reference missing");
            return;
        }

        if (WorldSaveManager.Instance == null)
        {
            Debug.LogWarning("[World] Cannot apply world seed - WorldSaveManager not available");
            return;
        }

        var metadata = WorldSaveManager.Instance.CurrentWorldMetadata ?? WorldSaveManager.Instance.GetCurrentWorldMetadata();
        if (metadata == null)
        {
            Debug.LogWarning("[World] No world metadata available - using existing terrain seed");
            return;
        }

        int worldSeed = metadata.WorldSeed;
        if (worldSeed == 0)
        {
            Debug.LogWarning("[World] World metadata seed not set - falling back to existing terrain seed");
            return;
        }

        if (config.noiseSeed != worldSeed)
        {
            Debug.Log($"[World] Applying world seed {worldSeed} (previous {config.noiseSeed})");
            config.noiseSeed = worldSeed;
        }
    }

    // REPLACE InitializeWorld() METHOD
    private void InitializeWorld()
    {
        // Validate manager dependencies
        bool managersMissing = 
            ChunkPoolManager.Instance == null || 
            MeshDataPool.Instance == null || 
            ChunkStateManager.Instance == null || 
            ChunkOperationsQueue.Instance == null;
            
        if (managersMissing)
        {
            Debug.LogError("Cannot initialize world - required managers not available");
            
            // Log specific missing managers
            if (ChunkPoolManager.Instance == null) Debug.LogError("ChunkPoolManager not available");
            if (MeshDataPool.Instance == null) Debug.LogError("MeshDataPool not available");
            if (ChunkStateManager.Instance == null) Debug.LogError("ChunkStateManager not available");
            if (ChunkOperationsQueue.Instance == null) Debug.LogError("ChunkOperationsQueue not available");
            
            return;
        }

        // Validate player controller
        if (playerController == null)
        {
            playerController = FindAnyObjectByType<ThirdPersonController>();
            if (playerController == null)
            {
                Debug.LogError("Cannot initialize world - PlayerController not found!");
                return;
            }
        }

        // Cache the operations queue reference
        operationsQueue = ChunkOperationsQueue.Instance;

        ApplyWorldSeedFromMetadata();
        ResetInitialLoadTracking();

        // Initialize with default coordinates if needed
        if (lastPlayerChunkCoordinates == Vector3Int.zero)
        {
            playerPosition = playerController.transform.position;
            lastPlayerChunkCoordinates = Coord.WorldToChunkCoord(playerPosition, chunkSize, voxelSize);
            Debug.Log($"Initialized player position: {playerPosition}, chunk: {lastPlayerChunkCoordinates}");
        }

        // Load initial chunks around player
        UpdateChunks(playerPosition);
        
        Debug.Log("World initialized successfully");
    }
    #endregion

    #region World State Updates

    public bool TryGetChunk(Vector3Int coordinate, out Chunk chunk)
    {
        return chunks.TryGetValue(coordinate, out chunk);
    }

    public void RegisterChunk(Vector3Int coord, Chunk chunk)
    {
        if (chunks.ContainsKey(coord))
        {
            Debug.LogWarning($"Chunk {coord} already in dictionary");
            return;
        }

        else
        {
            // If singleplayer or client-only usage, just re-parent
            chunk.transform.SetParent(this.transform, false);
        }

        chunk.gameObject.SetActive(true);

        chunks[coord] = chunk;
        
        // DIAGNOSTIC: Log for problematic coordinates
        if (IsProblematicCoordinate(coord))
        {
            Debug.Log($"[COORD_5_DEBUG] RegisterChunk - chunk {coord} registered at position {chunk.transform.position}");
        }
    }

    public bool IsSolidChunkMarkedForModification(Vector3Int chunkCoord)
    {
        return modifiedSolidChunks.Contains(chunkCoord);
    }

    public bool RemoveChunk(Vector3Int coordinate)
    {
        // CRITICAL DIAGNOSTIC: Log when chunks are removed, especially if they have pending updates
        bool hasPendingUpdates = HasPendingUpdates(coordinate);
        var state = ChunkStateManager.Instance?.GetChunkState(coordinate);
        
        if (hasPendingUpdates)
        {
            Debug.LogWarning($"[RemoveChunk] CRITICAL: Removing chunk {coordinate} with PENDING UPDATES! " +
                $"State: {state?.Status}, VoxelUpdates: {pendingVoxelUpdates.ContainsKey(coordinate)}, " +
                $"DensityUpdates: {pendingDensityPointUpdates.ContainsKey(coordinate)}");
            LogChunkTrace(coordinate, $"RemoveChunk: CRITICAL - Removing chunk with pending updates! State: {state?.Status}");
        }
        else
        {
            Debug.Log($"[RemoveChunk] Removing chunk {coordinate} (no pending updates, state: {state?.Status})");
            LogChunkTrace(coordinate, $"RemoveChunk: Removing chunk - no pending updates, state: {state?.Status}");
        }
        
        bool removed = chunks.Remove(coordinate);
        if (removed)
        {
            ClearForcedChunkFlag(coordinate, "Chunk removed from world");
        }
        return removed;
    }

    public bool IsChunkLoaded(Vector3Int chunkCoordinates)
    {
        return chunks.ContainsKey(chunkCoordinates);
    }

    public void ResetTerrainAnalysisCache()
    {
        // Clear cache state when switching worlds/seeds
        TerrainAnalysisCache.ResetCache();
        TerrainAnalysisCache.Update();

        Vector3Int sampleOrigin = lastPlayerChunkCoordinates;
        Vector3 referencePosition = playerPosition;
        if (referencePosition == Vector3.zero && playerController != null)
        {
            referencePosition = playerController.transform.position;
        }
        if (referencePosition != Vector3.zero)
        {
            sampleOrigin = Coord.WorldToChunkCoord(referencePosition, chunkSize, voxelSize);
        }

        for (int x = -1; x <= 1; x++)
        for (int z = -1; z <= 1; z++)
        {
            Vector3Int sampleCoord = sampleOrigin + new Vector3Int(x, 0, z);
            TerrainAnalysisCache.TryGetAnalysis(sampleCoord, out _);
        }

        Debug.Log("Reset TerrainAnalysisCache for newly loaded world");
    }

    public void UpdatePlayerPosition(Vector3 newPosition)
    {
        if (newPosition != playerPosition)
        {
            playerPosition = newPosition;
            
            Vector3Int newChunkCoord = Coord.WorldToChunkCoord(newPosition, chunkSize, voxelSize);
            if (newChunkCoord != lastPlayerChunkCoordinates || justStarted)
            {
                lastPlayerChunkCoordinates = newChunkCoord;
                justStarted = false;
                UpdateChunks(newChunkCoord);
            }
        }
    }
    
    private void UpdateWorldState()
    {
        // Check for valid initialization
        if (playerController == null)
        {
            playerController = FindAnyObjectByType<ThirdPersonController>();
            if (playerController == null)
            {
                Debug.LogWarning("[UpdateWorldState] still waiting on PlayerController");
                return;
            }
        }

        UpdatePlayerMovementLock(initialLoadInProgress);

        if (operationsQueue == null)
        {
            operationsQueue = ChunkOperationsQueue.Instance;
            if (operationsQueue == null)
            {
                Debug.LogError("ChunkOperationsQueue still null in UpdateWorldState");
                return;
            }
        }

        if (initialLoadInProgress)
        {
            CleanupStaleInitialLoadEntries();

            if (initialLoadStage == InitialLoadStage.ProcessingTerrainCache)
            {
                TerrainAnalysisCache.ProcessPendingSavesImmediate(InitialLoadTerrainCacheFlushBatch);
            }
            else if (initialLoadStage == InitialLoadStage.UnloadingEmptyChunks)
            {
                ProcessInitialLoadEmptyUnloads();
            }

            UpdateInitialLoadProgressState();
        }

        bool allowQueueProcessing = !initialLoadInProgress || initialLoadStage != InitialLoadStage.ProcessingTerrainCache;
        if (allowQueueProcessing)
        {
            operationsQueue.ProcessOperations();
        }

        if (initialLoadInProgress)
        {
            if (initialLoadStage == InitialLoadStage.UnloadingEmptyChunks)
            {
                bool hasPendingUnloads = initialLoadEmptyPendingUnload.Count > 0 || initialLoadEmptyUnloadQueue.Count > 0;
                bool queueBusy = operationsQueue != null && operationsQueue.HasPendingUnloadOperations();
                bool terrainPending = TerrainAnalysisCache.HasPendingWork();

                if (!hasPendingUnloads && !queueBusy && !terrainPending)
                {
                    CompleteInitialLoad();
                }
            }
            else if (initialLoadStage == InitialLoadStage.ProcessingTerrainCache)
            {
                if (!TerrainAnalysisCache.HasPendingWork() && TerrainAnalysisCache.GetPendingSaveCount() == 0)
                {
                    CompleteInitialLoad();
                }
            }
        }
        
        // Only update chunks if we have a valid player position
        bool allowChunkStreaming = !initialLoadInProgress || initialLoadStage == InitialLoadStage.LoadingChunks;
        if (allowChunkStreaming && playerPosition != Vector3.zero)
        {
            UpdateChunks(playerPosition);
        }

        RefreshNearbyChunkAccessTimes();
        ProcessPendingUpdates();
        ProcessMeshUpdates();
    }
    #endregion

    public void RegisterPlayerPosition(ulong clientId, Vector3 position)
    {
        activePlayerPositions[clientId] = position;
        
        Vector3Int chunkCoord = Coord.WorldToChunkCoord(position, chunkSize, voxelSize);
        playerChunkCoordinates[clientId] = chunkCoord;
        
        // Update all chunks if we're the server
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            UpdateGlobalChunkState();
        }
    }

    public void UnregisterPlayer(ulong clientId)
    {
        activePlayerPositions.Remove(clientId);
        playerChunkCoordinates.Remove(clientId);
        
        // Update all chunks if we're the server
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            UpdateGlobalChunkState();
        }
    }

    public void UpdatePlayerPositionForClient(ulong clientId, Vector3 position)
    {
        Vector3Int newChunkCoord = Coord.WorldToChunkCoord(position, chunkSize, voxelSize);
        
        // Store the position
        activePlayerPositions[clientId] = position;
        
        // Check if the player moved to a different chunk
        if (!playerChunkCoordinates.TryGetValue(clientId, out Vector3Int currentChunkCoord) || 
            newChunkCoord != currentChunkCoord)
        {
            // Store new chunk coordinate
            playerChunkCoordinates[clientId] = newChunkCoord;
            
            // Only update global chunks if we're the server
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                // Don't trigger a full reassessment every time - this will happen periodically anyway
                // Only log the update for debugging
                if (Time.frameCount % 300 == 0)  // Log every ~5 seconds at 60fps
                {
                    Debug.Log($"SERVER tracked player {clientId} moved to chunk {newChunkCoord}");
                }
            }
        }
    }
    public void UpdateGlobalChunkState()
    {
        // Skip if not the server
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
        
        // Recalculate the active chunks set
        RecalculateActiveChunks();
        
        // Get dynamic counts based on the configured value and current state
        int dynamicChunksPerFrame = Mathf.Max(1, MeshDataPool.Instance.GetDynamicChunksPerFrame());
        
        // Adjust load processing count based on configuration
        int loadChunksPerUpdate = dynamicChunksPerFrame;
        
        // For unloading, make it more aggressive if we have many chunks
        int additionalUnloads = Mathf.Max(0, Mathf.FloorToInt((chunks.Count - activeChunkCoords.Count) / 20f));
        int baseUnloadPerUpdate = Mathf.Max(0, Mathf.RoundToInt(dynamicChunksPerFrame * 0.75f));
        int unloadChunksPerUpdate = baseUnloadPerUpdate + additionalUnloads;
        
        bool pauseServerUnloadsDuringCacheStage = IsInitialLoadInProgress && initialLoadStage == InitialLoadStage.ProcessingTerrainCache;
        if (pauseServerUnloadsDuringCacheStage)
        {
            unloadChunksPerUpdate = 0;
        }
        else
        {
            unloadChunksPerUpdate = Mathf.Clamp(unloadChunksPerUpdate, 1, Mathf.Max(loadChunksPerUpdate, 64));
        }
        
        // Process chunk loading
        ProcessActiveChunksLoading(loadChunksPerUpdate);
        
        // Process chunk unloading
        ProcessServerChunkUnloading(unloadChunksPerUpdate);
        
        // Log the counts occasionally
        if (Time.frameCount % 300 == 0)
        {
            Debug.Log($"Global chunk update: Processing up to {loadChunksPerUpdate} loads and {unloadChunksPerUpdate} unloads per update");
        }
        
        // Update the last update time
        lastGlobalChunkUpdateTime = Time.time;
    }

    private void ProcessServerChunkUnloading(int maxUnloadsPerCall)
    {
        if (!NetworkManager.Singleton.IsServer) return;

        if (maxUnloadsPerCall <= 0)
        {
            return;
        }
        
        // Make a deterministic ordering of chunks to unload
        List<ChunkUnloadCandidate> unloadCandidates = new List<ChunkUnloadCandidate>();
        
        // Calculate time-based priority boost to ensure older chunks eventually get unloaded
        float currentTime = Time.time;
        
        // Collect candidates with eligibility check
        foreach (var chunkEntry in chunks)
        {
            Vector3Int chunkCoord = chunkEntry.Key;
            Chunk chunk = chunkEntry.Value;
            
            if (IsChunkEligibleForUnload(chunkCoord, chunk))
            {
                // Calculate priority based on age and distance
                float minDistanceToPlayers = float.MaxValue;
                
                foreach (var playerEntry in playerChunkCoordinates)
                {
                    Vector3Int playerChunk = playerEntry.Value;
                    float distance = Vector3Int.Distance(chunkCoord, playerChunk);
                    minDistanceToPlayers = Mathf.Min(minDistanceToPlayers, distance);
                }
                
                // Calculate age factor - older chunks get higher priority
                float ageFactor = (currentTime - chunk.lastAccessTime) / 60f; // Age in minutes
                
                // Modified chunks get slightly lower priority unless they're very old
                float modifiedPenalty = 0f;
                if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis.WasModified)
                {
                    modifiedPenalty = Math.Max(0f, 5f - ageFactor); // Penalty decreases with age
                }
                
                // Calculate final priority score
                float priority = minDistanceToPlayers + (ageFactor * 3f) - modifiedPenalty;
                
                unloadCandidates.Add(new ChunkUnloadCandidate(
                    chunkCoord, 
                    priority,
                    minDistanceToPlayers,
                    ageFactor
                ));
            }
        }
        
        // Sort by priority (highest first)
        unloadCandidates.Sort((a, b) => b.priority.CompareTo(a.priority));
        
        // Limit unloads per call
        int maxUnloads = Mathf.Clamp(maxUnloadsPerCall, 0, unloadCandidates.Count);
        int unloadCount = 0;
        
        for (int i = 0; i < unloadCandidates.Count && unloadCount < maxUnloads; i++)
        {
            operationsQueue.QueueChunkForUnload(unloadCandidates[i].chunkCoord);
            unloadCount++;
            
            // Log the first and last chunk being unloaded (safely)
            if (i == 0 || (i == maxUnloads-1 && i < unloadCandidates.Count-1) || i == unloadCandidates.Count-1)
            {
                Debug.Log($"[SERVER] Unloading chunk {unloadCandidates[i].chunkCoord}, " +
                        $"distance: {unloadCandidates[i].distanceToPlayers:F1}, " +
                        $"age: {unloadCandidates[i].ageFactor:F1}min");
            }
        }

        // Log the unloading process occasionally for debugging
        if (Time.frameCount % 300 == 0)
        {
            int outOfRangeCount = 0;
            int totalLoadedCount = chunks.Count;
            
            foreach (var chunkEntry in chunks)
            {
                Vector3Int chunkCoord = chunkEntry.Key;
                float minDistToPlayer = float.MaxValue;
                
                foreach (var playerEntry in playerChunkCoordinates)
                {
                    Vector3Int playerChunk = playerEntry.Value;
                    float distance = Vector3Int.Distance(chunkCoord, playerChunk);
                    minDistToPlayer = Mathf.Min(minDistToPlayer, distance);
                }
                
                if (minDistToPlayer > unloadRadius)
                {
                    outOfRangeCount++;
                }
            }
            
            Debug.Log($"SERVER chunk unloading stats: {unloadCount} chunks queued for unload" +
                    $" | {totalLoadedCount} total loaded chunks" +
                    $" | {outOfRangeCount} chunks out of player range" +
                    $" | {unloadCandidates.Count} unload candidates");
        }
    }

    private void ProcessActiveChunksLoading(int maxPerCall)
    {
        // Create a list of chunks to load, sorted by priority
        List<Vector3Int> loadCandidates = new List<Vector3Int>();
        
        foreach (var chunkCoord in activeChunkCoords)
        {
            // Skip if already loaded or being loaded
            if (chunks.ContainsKey(chunkCoord) || 
                operationsQueue.HasPendingLoadOperation(chunkCoord) ||
                ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord))
                continue;
                
            // Skip if in a state that doesn't allow loading
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            if (state.Status != ChunkConfigurations.ChunkStatus.None && 
                state.Status != ChunkConfigurations.ChunkStatus.Unloaded)
                continue;
                
            // Calculate priority based on minimum distance to any player
            float minDistance = float.MaxValue;
            foreach (var entry in playerChunkCoordinates)
            {
                Vector3Int playerChunk = entry.Value;
                float distance = Vector3Int.Distance(chunkCoord, playerChunk);
                minDistance = Mathf.Min(minDistance, distance);
            }
            
            // Add to candidates with priority
            loadCandidates.Add(chunkCoord);
        }
        
        // Sort by distance to player (closest first)
        loadCandidates.Sort((a, b) => {
            float distA = GetMinDistanceToPlayers(a);
            float distB = GetMinDistanceToPlayers(b);
            return distA.CompareTo(distB);
        });
        
        // Load chunks up to the limit
        int count = Mathf.Min(loadCandidates.Count, maxPerCall);
        for (int i = 0; i < count; i++)
        {
            Vector3Int coord = loadCandidates[i];
            // Prioritize chunks that are very close to players
            bool immediate = GetMinDistanceToPlayers(coord) <= ImmediateLoadDistanceThreshold;
            operationsQueue.QueueChunkForLoad(coord, immediate, quickCheck: !immediate);
        }
        
        if (count > 0 && Time.frameCount % 300 == 0)
        {
            Debug.Log($"Queued {count} chunks for loading out of {loadCandidates.Count} candidates");
        }
    }

    private float GetMinDistanceToPlayers(Vector3Int chunkCoord)
    {
        float minDistance = float.MaxValue;
        foreach (var entry in playerChunkCoordinates)
        {
            Vector3Int playerChunk = entry.Value;
            float distance = Vector3Int.Distance(chunkCoord, playerChunk);
            minDistance = Mathf.Min(minDistance, distance);
        }
        return minDistance;
    }

    private bool IsChunkEligibleForUnload(Vector3Int chunkCoord, Chunk chunk)
    {
        // Skip if in active chunks set
        if (activeChunkCoords.Contains(chunkCoord))
            return false;
            
        // Skip if has pending updates
        if (HasPendingUpdates(chunkCoord))
            return false;
            
        // Skip if in quarantine
        if (ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord))
            return false;
            
        // Check current chunk state
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
        if (state.Status != ChunkConfigurations.ChunkStatus.Loaded && 
            state.Status != ChunkConfigurations.ChunkStatus.Modified)
            return false;
            
        // Calculate minimum distance to any player
        float minDistanceToPlayers = float.MaxValue;
        
        foreach (var playerEntry in playerChunkCoordinates)
        {
            Vector3Int playerChunk = playerEntry.Value;
            float distance = Vector3Int.Distance(chunkCoord, playerChunk);
            minDistanceToPlayers = Mathf.Min(minDistanceToPlayers, distance);
        }
        
        // Modified chunks get special treatment - don't unload if close to unload radius
        if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis.WasModified)
        {
            // Don't unload modified chunks unless they're well beyond the unload radius
            return minDistanceToPlayers > unloadRadius * 1.25f;
        }
        
        // Standard check - outside any player's unload radius
        return minDistanceToPlayers > unloadRadius;
    }

    public void RecalculateActiveChunks()
    {
        // Clear the current active chunks set
        activeChunkCoords.Clear();
        
        // For each player, add their relevant chunks to the active set
        foreach (var entry in playerChunkCoordinates)
        {
            Vector3Int playerChunkCoord = entry.Value;
            
            // Always add the center chunk and its immediate neighbors (most critical for gameplay)
            activeChunkCoords.Add(playerChunkCoord);
            
            // Add immediate neighbors in all directions (3x3x3 cube)
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
            for (int y = -1; y <= 1; y++)
            {
                activeChunkCoords.Add(playerChunkCoord + new Vector3Int(x, y, z));
            }
            
            // Add remaining chunks in the load radius
            int loadRadiusSquared = loadRadius * loadRadius;
            
            for (int x = -loadRadius; x <= loadRadius; x++)
            {
                int dxSquared = x * x;
                
                for (int z = -loadRadius; z <= loadRadius; z++)
                {
                    int dzSquared = z * z;
                    int distanceSquared = dxSquared + dzSquared;
                    
                    if (distanceSquared <= loadRadiusSquared)
                    {
                        for (int y = -verticalLoadRadius; y <= verticalLoadRadius; y++)
                        {
                            // Skip center and immediate neighbors (already added)
                            if (Mathf.Abs(x) <= 1 && Mathf.Abs(z) <= 1 && Mathf.Abs(y) <= 1)
                                continue;
                                    
                            activeChunkCoords.Add(playerChunkCoord + new Vector3Int(x, y, z));
                        }
                    }
                }
            }
        }
        
        // Also add any chunks with pending updates to active set
        // This prevents unloading chunks that are waiting for operation completion
        foreach (var chunkEntry in chunks)
        {
            Vector3Int chunkCoord = chunkEntry.Key;
            
            if (HasPendingUpdates(chunkCoord))
            {
                activeChunkCoords.Add(chunkCoord);
            }
        }
        
        // Also add modified chunks to the active set
        foreach (var chunkEntry in chunks)
        {
            if (TerrainAnalysisCache.TryGetAnalysis(chunkEntry.Key, out var analysis) && analysis.WasModified)
            {
                // For now, prioritize keeping modified chunks unless very far from players
                bool anyPlayerNearby = false;
                foreach (var entry in playerChunkCoordinates)
                {
                    Vector3Int playerChunk = entry.Value;
                    float distanceToPlayer = Vector3Int.Distance(chunkEntry.Key, playerChunk);
                    if (distanceToPlayer <= unloadRadius * 1.5f)
                    {
                        anyPlayerNearby = true;
                        break;
                    }
                }
                
                if (anyPlayerNearby)
                {
                    activeChunkCoords.Add(chunkEntry.Key);
                }
            }
        }
        
        // Log the current number of active chunks occasionally
        if (Time.frameCount % 300 == 0)
        {
            Debug.Log($"Active chunks recalculated: {activeChunkCoords.Count} active chunks for {playerChunkCoordinates.Count} players");
        }
    }

    #region Initial Load Tracking
    private void ResetInitialLoadTracking()
    {
        initialLoadInProgress = true;
        initialLoadProgress = 0f;
        initialLoadStage = InitialLoadStage.LoadingChunks;
        initialLoadTargets.Clear();
        initialLoadPending.Clear();
        initialLoadEmptyUnloadQueue.Clear();
        initialLoadEmptyQueued.Clear();
        initialLoadEmptyTracked.Clear();
        initialLoadEmptyPendingUnload.Clear();
        initialLoadEmptyTotal = 0;
        initialLoadEmptyProcessed = 0;
        initialLoadTerrainTotal = 0;
        initialLoadTerrainProcessed = 0;
        TerrainAnalysisCache.SetSynchronousFlushMode(false);
        initialLoadStartTime = Time.time;
        initialLoadCompletionBroadcasted = false;
        UpdateInitialLoadUI("Preparing terrain...");
        UpdatePlayerMovementLock(true);
    }

    public void OnInitialChunkLoadQueued(Vector3Int chunkCoord)
    {
        if (!initialLoadInProgress)
            return;

        if (initialLoadTargets.Add(chunkCoord))
        {
            initialLoadPending.Add(chunkCoord);
            UpdateInitialLoadProgressState();
        }
    }

    private void HandleInitialLoadChunkReady(Vector3Int chunkCoord)
    {
        if (!initialLoadInProgress)
            return;

        if (initialLoadPending.Remove(chunkCoord))
        {
            UpdateInitialLoadProgressState();
        }
    }

    private void QueueInitialLoadEmptyChunk(Vector3Int chunkCoord)
    {
        if (initialLoadStage == InitialLoadStage.Complete)
            return;

        bool newlyTracked = initialLoadEmptyTracked.Add(chunkCoord);
        if (newlyTracked)
        {
            initialLoadEmptyTotal++;
        }

        if (initialLoadEmptyPendingUnload.Contains(chunkCoord))
        {
            UpdateInitialLoadProgressState();
            return;
        }

        if (!initialLoadEmptyQueued.Contains(chunkCoord))
        {
            initialLoadEmptyQueued.Add(chunkCoord);
            initialLoadEmptyUnloadQueue.Enqueue(chunkCoord);
        }

        initialLoadEmptyPendingUnload.Add(chunkCoord);
        UpdateInitialLoadProgressState();
    }

    private void ProcessInitialLoadEmptyUnloads(int overrideBudget = -1)
    {
        if (operationsQueue == null || initialLoadEmptyUnloadQueue.Count == 0)
            return;

        bool isDuringInitialLoad = initialLoadInProgress && initialLoadStage == InitialLoadStage.UnloadingEmptyChunks;
        int budget;

        if (overrideBudget > 0)
        {
            budget = overrideBudget;
        }
        else if (isDuringInitialLoad)
        {
            budget = Mathf.Max(initialLoadEmptyPendingUnload.Count, initialLoadEmptyUnloadQueue.Count);
        }
        else
        {
            budget = Mathf.Max(1, InitialLoadChunkBudget);
        }
        int processedThisFrame = 0;
        int iterations = 0;
        int maxIterations = Mathf.Max(initialLoadEmptyUnloadQueue.Count, budget * 2);

        while (processedThisFrame < budget && initialLoadEmptyUnloadQueue.Count > 0 && iterations < maxIterations)
        {
            Vector3Int chunkCoord = initialLoadEmptyUnloadQueue.Dequeue();
            initialLoadEmptyQueued.Remove(chunkCoord);
            iterations++;

            if (!initialLoadEmptyPendingUnload.Contains(chunkCoord))
                continue;

            if (!chunks.TryGetValue(chunkCoord, out Chunk chunk) || chunk == null)
            {
                if (NotifyInitialEmptyChunkUnloaded(chunkCoord))
                {
                    processedThisFrame++;
                }
                continue;
            }

            if (HasPendingUpdates(chunkCoord))
            {
                if (!initialLoadEmptyQueued.Contains(chunkCoord))
                {
                    initialLoadEmptyQueued.Add(chunkCoord);
                    initialLoadEmptyUnloadQueue.Enqueue(chunkCoord);
                }
                continue;
            }

            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            if (state.Status == ChunkConfigurations.ChunkStatus.Loaded ||
                state.Status == ChunkConfigurations.ChunkStatus.Modified)
            {
                operationsQueue.QueueChunkForUnload(chunkCoord);
                processedThisFrame++;
            }
        }

        if (processedThisFrame > 0)
        {
            UpdateInitialLoadProgressState();
        }
    }

    private void UpdateInitialLoadProgressState()
    {
        switch (initialLoadStage)
        {
            case InitialLoadStage.LoadingChunks:
            {
                int total = initialLoadTargets.Count;
                if (total <= 0)
                {
                    initialLoadProgress = 0f;
                }
                else
                {
                    initialLoadProgress = Mathf.Clamp01(1f - (initialLoadPending.Count / (float)total));
                }

                if (initialLoadPending.Count == 0 && total > 0)
                {
                    int pendingTerrain = TerrainAnalysisCache.GetPendingSaveCount();
                    bool terrainWorkPending = pendingTerrain > 0 || TerrainAnalysisCache.HasPendingWork();
                    bool hasEmptyWork = initialLoadEmptyTracked.Count > 0 ||
                                        initialLoadEmptyPendingUnload.Count > 0 ||
                                        initialLoadEmptyUnloadQueue.Count > 0;

                    if (terrainWorkPending)
                    {
                        initialLoadStage = InitialLoadStage.ProcessingTerrainCache;
                        initialLoadTerrainTotal = Mathf.Max(pendingTerrain, 1);
                        initialLoadTerrainProcessed = Mathf.Clamp(initialLoadTerrainTotal - pendingTerrain, 0, initialLoadTerrainTotal);
                        TerrainAnalysisCache.SetSynchronousFlushMode(true);
                        initialLoadProgress = initialLoadTerrainTotal > 0
                            ? Mathf.Clamp01(initialLoadTerrainProcessed / (float)initialLoadTerrainTotal)
                            : 0f;
                    }
                    else if (hasEmptyWork)
                    {
                        initialLoadStage = InitialLoadStage.UnloadingEmptyChunks;
                        int effectiveTotal = Mathf.Max(initialLoadEmptyTracked.Count,
                            initialLoadEmptyProcessed + initialLoadEmptyPendingUnload.Count);
                        initialLoadProgress = effectiveTotal > 0
                            ? Mathf.Clamp01(initialLoadEmptyProcessed / (float)effectiveTotal)
                            : 0f;
                    }
                    else
                    {
                        CompleteInitialLoad();
                    }
                }
                break;
            }
            case InitialLoadStage.ProcessingTerrainCache:
            {
                int pendingTerrain = Mathf.Max(0, TerrainAnalysisCache.GetPendingSaveCount());
                initialLoadTerrainTotal = Mathf.Max(initialLoadTerrainTotal, pendingTerrain);
                if (initialLoadTerrainTotal <= 0)
                {
                    initialLoadTerrainTotal = 1;
                }
                initialLoadTerrainProcessed = Mathf.Clamp(initialLoadTerrainTotal - pendingTerrain, 0, initialLoadTerrainTotal);

                initialLoadProgress = initialLoadTerrainTotal > 0
                    ? Mathf.Clamp01(initialLoadTerrainProcessed / (float)initialLoadTerrainTotal)
                    : 1f;

                if (pendingTerrain == 0 && !TerrainAnalysisCache.HasPendingWork())
                {
                    TerrainAnalysisCache.SetSynchronousFlushMode(false);
                    initialLoadTerrainProcessed = initialLoadTerrainTotal;

                    bool hasEmptyWork = initialLoadEmptyTracked.Count > 0 ||
                                        initialLoadEmptyPendingUnload.Count > 0 ||
                                        initialLoadEmptyUnloadQueue.Count > 0;

                    if (hasEmptyWork)
                    {
                        initialLoadStage = InitialLoadStage.UnloadingEmptyChunks;
                        int effectiveTotal = Mathf.Max(initialLoadEmptyTracked.Count,
                            initialLoadEmptyProcessed + initialLoadEmptyPendingUnload.Count);
                        initialLoadProgress = effectiveTotal > 0
                            ? Mathf.Clamp01(initialLoadEmptyProcessed / (float)effectiveTotal)
                            : 0f;
                    }
                    else
                    {
                        CompleteInitialLoad();
                    }
                }
                break;
            }
            case InitialLoadStage.UnloadingEmptyChunks:
            {
                int pendingCount = initialLoadEmptyPendingUnload.Count;
                int effectiveTotal = Mathf.Max(initialLoadEmptyTracked.Count, initialLoadEmptyProcessed + pendingCount);

                if (effectiveTotal <= 0)
                {
                    bool hasPending = pendingCount > 0 || initialLoadEmptyUnloadQueue.Count > 0;
                    initialLoadProgress = hasPending ? 0f : 1f;
                    if (!hasPending)
                    {
                        CompleteInitialLoad();
                    }
                }
                else
                {
                    initialLoadProgress = Mathf.Clamp01(initialLoadEmptyProcessed / (float)effectiveTotal);
                    if (initialLoadEmptyProcessed >= effectiveTotal &&
                        pendingCount == 0 &&
                        initialLoadEmptyUnloadQueue.Count == 0)
                    {
                        CompleteInitialLoad();
                    }
                }
                break;
            }
        }

        UpdateInitialLoadUI();
    }

    private void CompleteInitialLoad()
    {
        if (!initialLoadInProgress && initialLoadStage == InitialLoadStage.Complete)
            return;

        int pendingTerrain = TerrainAnalysisCache.GetPendingSaveCount();
        bool terrainWorkPending = pendingTerrain > 0 || TerrainAnalysisCache.HasPendingWork();

        if (terrainWorkPending)
        {
            if (initialLoadStage != InitialLoadStage.ProcessingTerrainCache)
            {
                initialLoadStage = InitialLoadStage.ProcessingTerrainCache;
                TerrainAnalysisCache.SetSynchronousFlushMode(true);
            }

            initialLoadTerrainTotal = Mathf.Max(initialLoadTerrainTotal, pendingTerrain);
            if (initialLoadTerrainTotal <= 0)
            {
                initialLoadTerrainTotal = 1;
            }
            initialLoadTerrainProcessed = Mathf.Clamp(initialLoadTerrainTotal - pendingTerrain, 0, initialLoadTerrainTotal);

            initialLoadInProgress = true;
            UpdateInitialLoadProgressState();
            return;
        }

        int pendingEmptyCount = initialLoadEmptyPendingUnload.Count;
        int effectiveEmptyTotal = Mathf.Max(initialLoadEmptyTracked.Count, initialLoadEmptyProcessed + pendingEmptyCount);
        bool hasPendingEmptyWork = pendingEmptyCount > 0 ||
                                   initialLoadEmptyUnloadQueue.Count > 0 ||
                                   effectiveEmptyTotal > initialLoadEmptyProcessed ||
                                   (operationsQueue != null && operationsQueue.HasPendingUnloadOperations());

        if (hasPendingEmptyWork)
        {
            if (initialLoadStage != InitialLoadStage.UnloadingEmptyChunks)
            {
                initialLoadStage = InitialLoadStage.UnloadingEmptyChunks;
            }

            initialLoadProgress = effectiveEmptyTotal > 0
                ? Mathf.Clamp01(initialLoadEmptyProcessed / (float)effectiveEmptyTotal)
                : 0f;
            UpdateInitialLoadUI();
            return;
        }

        initialLoadStage = InitialLoadStage.Complete;
        initialLoadInProgress = false;
        initialLoadProgress = 1f;

        TerrainAnalysisCache.SetSynchronousFlushMode(false);
        initialLoadTerrainTotal = 0;
        initialLoadTerrainProcessed = 0;

        if (!initialLoadCompletionBroadcasted)
        {
            initialLoadCompletionBroadcasted = true;
            float elapsed = initialLoadStartTime > 0f ? Time.time - initialLoadStartTime : 0f;
            Debug.Log($"Initial world load completed in {elapsed:F1}s ({initialLoadTargets.Count} chunks).");
        }

        initialLoadEmptyPendingUnload.Clear();
        initialLoadEmptyUnloadQueue.Clear();
        initialLoadEmptyQueued.Clear();

        UpdatePlayerMovementLock(false);
        UpdateInitialLoadUI("World ready");
    }

    private void UpdateInitialLoadUI(string statusOverride = null)
    {
        if (GameUIManager.Instance == null)
            return;

        bool show = initialLoadInProgress;
        string status = statusOverride;

        if (string.IsNullOrEmpty(status))
        {
            switch (initialLoadStage)
            {
                case InitialLoadStage.LoadingChunks:
                {
                    int loaded = initialLoadTargets.Count - initialLoadPending.Count;
                    int total = initialLoadTargets.Count;
                    status = total > 0
                        ? $"Loading terrain {loaded}/{total}"
                        : "Preparing terrain...";
                    break;
                }
                case InitialLoadStage.ProcessingTerrainCache:
                {
                    int pending = Mathf.Max(0, TerrainAnalysisCache.GetPendingSaveCount());
                    int displayTotal = Mathf.Max(initialLoadTerrainTotal, pending);
                    if (displayTotal <= 0)
                    {
                        displayTotal = 1;
                    }
                    int displayProcessed = Mathf.Clamp(displayTotal - pending, 0, displayTotal);
                    status = displayTotal > 0
                        ? $"Caching terrain {displayProcessed}/{displayTotal}"
                        : "Caching terrain...";
                    break;
                }
                case InitialLoadStage.UnloadingEmptyChunks:
                {
                    int displayTotal = Mathf.Max(initialLoadEmptyTotal, initialLoadEmptyProcessed + initialLoadEmptyPendingUnload.Count);
                    status = displayTotal > 0
                        ? $"Unloading empty chunks {initialLoadEmptyProcessed}/{displayTotal}"
                        : "Unloading empty chunks...";
                    break;
                }
                case InitialLoadStage.Complete:
                default:
                {
                    status = "World ready";
                    break;
                }
            }
        }

        float progressValue = Mathf.Clamp01(initialLoadProgress);

        GameUIManager.Instance.SetGameplayLoadingOverlay(show, progressValue, status);
    }
    
    private void CleanupStaleInitialLoadEntries()
    {
        if (!initialLoadInProgress || initialLoadPending.Count == 0)
            return;

        if (operationsQueue == null)
        {
            operationsQueue = ChunkOperationsQueue.Instance;
        }

        List<Vector3Int> staleEntries = null;
        var pendingSnapshot = initialLoadPending.ToArray();

        foreach (var chunkCoord in pendingSnapshot)
        {
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            bool chunkExists = chunks.ContainsKey(chunkCoord);
            bool hasActiveLoad = operationsQueue != null && operationsQueue.HasPendingLoadOperation(chunkCoord);
            bool isQuarantined = ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord);

            bool stillLoading = state.Status == ChunkConfigurations.ChunkStatus.Loading ||
                                state.Status == ChunkConfigurations.ChunkStatus.None;

            if (stillLoading && (hasActiveLoad || chunkExists))
            {
                continue;
            }

            bool shouldComplete = false;

            if (chunkExists && (state.Status == ChunkConfigurations.ChunkStatus.Loaded ||
                                state.Status == ChunkConfigurations.ChunkStatus.Modified))
            {
                shouldComplete = true;
            }
            else if (!chunkExists && !hasActiveLoad &&
                     (state.Status == ChunkConfigurations.ChunkStatus.Unloaded ||
                      state.Status == ChunkConfigurations.ChunkStatus.Unloading))
            {
                shouldComplete = true;
            }
            else if (isQuarantined)
            {
                shouldComplete = true;
            }

            if (shouldComplete)
            {
                staleEntries ??= new List<Vector3Int>();
                staleEntries.Add(chunkCoord);
            }
        }

        if (staleEntries == null || staleEntries.Count == 0)
            return;

        foreach (var chunkCoord in staleEntries)
        {
            if (initialLoadPending.Remove(chunkCoord))
            {
                if (config != null && config.enableChunkLifecycleLogs)
                {
                    var state = ChunkStateManager.Instance.GetChunkState(chunkCoord).Status;
                    Debug.LogWarning($"[InitialLoad] Marking stale pending chunk {chunkCoord} as complete (state: {state})");
                }
            }
        }

        UpdateInitialLoadProgressState();
    }
    #endregion

    #region Density Handling
    private Vector3Int TranslatePositionToChunk(Vector3 worldPos, Vector3Int targetChunkCoord)
    {
        // Get world position of the target chunk's origin
        Vector3 targetChunkOrigin = GetChunkWorldPosition(targetChunkCoord);
        
        // Calculate local position in the target chunk's coordinate system
        Vector3 localPos = worldPos - targetChunkOrigin;
        
        // Convert to voxel coordinates in the target chunk
        Vector3Int voxelPos = new Vector3Int(
            Mathf.FloorToInt(localPos.x / voxelSize),
            Mathf.FloorToInt(localPos.y / voxelSize),
            Mathf.FloorToInt(localPos.z / voxelSize)
        );
        
        return voxelPos;
    }


    public void HandleVoxelDestruction(Vector3Int chunkCoord, Vector3Int voxelPos)
    {   
        if (currentlyProcessingChunks.Contains(chunkCoord))
        {
            Debug.LogWarning($"Already processing chunk {chunkCoord}, skipping");
            return;
        }
        
        currentlyProcessingChunks.Add(chunkCoord);
        
        try {
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            if (!chunks.TryGetValue(chunkCoord, out Chunk chunk) || 
                (state.Status != ChunkConfigurations.ChunkStatus.Loaded && 
                state.Status != ChunkConfigurations.ChunkStatus.Modified))
            {
                Debug.LogWarning($"Cannot handle voxel destruction - chunk {chunkCoord} not in valid state. Current state: {state.Status}");
                currentlyProcessingChunks.Remove(chunkCoord);
                return;
            }

            // Ensure chunk data is initialized
            chunk.EnsureDataInitialized();
            
            // Ensure jobs are completed before proceeding
            chunk.CompleteAllJobs();

            // Calculate the affected area with extra radius for boundary cases
            Vector3 worldPos = Coord.GetWorldPosition(chunkCoord, voxelPos, chunkSize, voxelSize);
            float radius = voxelSize * (Config.densityInfluenceRadius + 1f); // Extra radius for boundary cases
            var affectedChunks = GetAffectedChunks(worldPos, radius);
            Dictionary<Vector3Int, bool> chunkForceFalloffFlags = new Dictionary<Vector3Int, bool>();
            
            // Auto-enable tracing if enabled in inspector
            if (autoTraceMiningOperations)
            {
                foreach (var neighborCoord in affectedChunks)
                {
                    EnableChunkTracing(neighborCoord);
                }
            }
            
            // CRITICAL FIX: Always invalidate terrain analysis for ALL affected chunks
            // AND queue density updates IMMEDIATELY to prevent race conditions with QuickCheck
            Debug.Log($"[HandleVoxelDestruction] Processing {affectedChunks.Count} affected chunks for mining at {worldPos}");
            foreach (var neighborCoord in affectedChunks)
            {
                LogChunkTrace(neighborCoord, $"Mining operation affecting this chunk at worldPos {worldPos}");

                bool cachedAnalysisAvailable = TerrainAnalysisCache.TryGetAnalysis(neighborCoord, out var cachedAnalysis);
                bool chunkWasSolid = cachedAnalysisAvailable && cachedAnalysis.IsSolid;

                float distanceToChunk = DistanceToChunkBounds(worldPos, neighborCoord);
                bool withinRadius = distanceToChunk <= radius + voxelSize;
                bool forceBoundaryFalloff = chunkWasSolid && !withinRadius;
                chunkForceFalloffFlags[neighborCoord] = forceBoundaryFalloff;

                if (forceBoundaryFalloff && ShouldLogChunkDiagnostics(neighborCoord))
                {
                    Debug.Log($"[MOD_DIAG:{neighborCoord}] HandleVoxelDestruction forcing boundary falloff (distance={distanceToChunk:F2}, radius={radius:F2})");
                }

                // Always invalidate the analysis - this ensures we don't skip loading modified chunks
                TerrainAnalysisCache.InvalidateAnalysis(neighborCoord);
                Debug.Log($"[HandleVoxelDestruction] Invalidated terrain cache for chunk {neighborCoord}");
                LogChunkTrace(neighborCoord, "Terrain cache invalidated");
                
                // CRITICAL FIX: Also mark immediate neighbors of affected chunks for modification
                // This ensures chunks like (5, 0, -1) that are vertically adjacent to modified chunks
                // get loaded even if they're not directly within the mining radius
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue; // Skip center
                    
                    Vector3Int neighborOfNeighbor = neighborCoord + new Vector3Int(dx, dy, dz);
                    
                    // Skip if this neighbor is already in the affected chunks list
                    if (affectedChunks.Contains(neighborOfNeighbor))
                        continue;
                    
                    var neighborState = ChunkStateManager.Instance.GetChunkState(neighborOfNeighbor);
                    
                    // CRITICAL FIX: Check if this neighbor needs to be marked for modification
                    // This is especially important for vertically adjacent chunks (dy != 0)
                    // We need to handle multiple scenarios:
                    // 1. Solid chunks (from cache) - definitely need modification
                    // 2. Chunks without cache data - might need modification if adjacent to modified chunk
                    // 3. Chunks currently loading - need cache invalidation to prevent stale data
                    bool isSolidChunk = false;
                    bool hasCacheData = TerrainAnalysisCache.TryGetAnalysis(neighborOfNeighbor, out var neighborAnalysis);
                    if (hasCacheData)
                    {
                        isSolidChunk = neighborAnalysis.IsSolid;
                    }
                    
                    // CRITICAL FIX: Check distance BEFORE marking for modification
                    // The old logic would mark ALL solid neighbors, causing chunks like (-5, -1, -1)
                    // to get marked even when mining was 23+ units away. Once marked, they stayed
                    // in the "modified solid chunks" set forever and got bad worldPos values.
                    float distanceToNeighborForMarking = DistanceToChunkBounds(worldPos, neighborOfNeighbor);
                    float maxMarkingDistance = radius * 3f; // More generous than the density update check
                    
                    // Mark for modification if:
                    // - It's a solid chunk (from cache) AND within reasonable distance
                    // - It's vertically adjacent and we don't have cache data AND within distance
                    // - It's directly adjacent to an affected chunk AND within distance
                    bool shouldMarkForModification = false;
                    if (isSolidChunk && distanceToNeighborForMarking <= maxMarkingDistance)
                    {
                        shouldMarkForModification = true;
                        Debug.Log($"[HandleVoxelDestruction] Marking neighbor {neighborOfNeighbor} (solid, dist: {distanceToNeighborForMarking:F2})");
                    }
                    else if (!hasCacheData && (dy != 0 || Mathf.Abs(dx) + Mathf.Abs(dz) <= 1) && distanceToNeighborForMarking <= maxMarkingDistance)
                    {
                        shouldMarkForModification = true;
                        Debug.Log($"[HandleVoxelDestruction] Marking neighbor {neighborOfNeighbor} (no cache, dist: {distanceToNeighborForMarking:F2})");
                    }
                    else if (isSolidChunk || !hasCacheData)
                    {
                        // This is the FIX - don't mark if too far!
                        Debug.Log($"[HandleVoxelDestruction] SKIPPING mark for {neighborOfNeighbor} - too far (dist: {distanceToNeighborForMarking:F2} > max: {maxMarkingDistance:F2})");
                    }
                    
                    if (shouldMarkForModification)
                    {
                        MarkChunkForForcedLoad(neighborOfNeighbor, $"Boundary neighbor of {neighborCoord}");
                        LogChunkTrace(neighborOfNeighbor, $"Marked for modification - neighbor of affected chunk {neighborCoord}");
                        if (isSolidChunk)
                        {
                            MarkSolidChunkForModification(neighborOfNeighbor);
                        }
                        
                        // Also invalidate its cache
                        TerrainAnalysisCache.InvalidateAnalysis(neighborOfNeighbor);
                        
                        // CRITICAL FIX: Only queue density update if the mining position is actually close enough
                        // to potentially affect this neighbor chunk. Otherwise we get updates with positions way
                        // outside the chunk bounds (e.g., local pos (37, 25, 0) when valid range is 0-16)
                        float distanceToNeighbor = DistanceToChunkBounds(worldPos, neighborOfNeighbor);
                        float maxEffectiveDistance = radius * 2f; // Allow some extra margin for boundary effects
                        
                        // DIAGNOSTIC: Always log this check for traced chunks
                        if (ShouldLogChunkDiagnostics(neighborOfNeighbor))
                        {
                            Debug.Log($"[MOD_DIAG:{neighborOfNeighbor}] Distance check in HandleVoxelDestruction - " +
                                $"worldPos: {worldPos}, distanceToNeighbor: {distanceToNeighbor:F2}, maxEffectiveDistance: {maxEffectiveDistance:F2}, " +
                                $"Within range: {distanceToNeighbor <= maxEffectiveDistance}");
                        }
                        
                        if (distanceToNeighbor <= maxEffectiveDistance)
                        {
                            QueueDensityUpdate(neighborOfNeighbor, worldPos);
                        }
                        else
                        {
                            Debug.Log($"[HandleVoxelDestruction] Skipping density update for neighbor {neighborOfNeighbor} - " +
                                $"mining position {worldPos} is too far (distance: {distanceToNeighbor:F2}, max: {maxEffectiveDistance:F2})");
                        }
                        
                        // Request immediate load if not already loaded
                        if (!chunks.ContainsKey(neighborOfNeighbor))
                        {
                            RequestImmediateChunkLoad(neighborOfNeighbor);
                        }
                    }
                    // If neighbor of neighbor is currently loading, invalidate its cache too
                    // This prevents it from caching stale data about the neighbor we're modifying
                    else if (neighborState.Status == ChunkConfigurations.ChunkStatus.Loading)
                    {
                        TerrainAnalysisCache.InvalidateAnalysis(neighborOfNeighbor);
                        Debug.Log($"[HandleVoxelDestruction] Invalidated terrain cache for loading neighbor-of-neighbor chunk {neighborOfNeighbor}");
                    }
                }
                
                // CRITICAL: Queue density update IMMEDIATELY before checking if chunk is loaded
                // This ensures QuickCheck will see pending updates even if chunk is already queued for loading
                Debug.Log($"[HandleVoxelDestruction] Queuing density update for affected chunk {neighborCoord}");
                LogChunkTrace(neighborCoord, $"Queuing density update from HandleVoxelDestruction");
                QueueDensityUpdate(neighborCoord, worldPos);
                
                // Check for solid chunks and mark them for modification
                if (TerrainAnalysisCache.TryGetAnalysis(neighborCoord, out var analysis) && analysis.IsSolid)
                {
                    Debug.Log($"Found solid neighbor at {neighborCoord}, marking for modification");
                    MarkSolidChunkForModification(neighborCoord);
                    
                    // Queue immediate load for solid chunks
                    if (!chunks.ContainsKey(neighborCoord))
                    {
                        LoadChunkImmediately(neighborCoord);
                    }
                }
            }
            
            // Create a filtered list of chunks that are loaded and can be modified
            var chunksToModify = new HashSet<Vector3Int>();
            foreach (var neighborCoord in affectedChunks)
            {
                if (chunks.ContainsKey(neighborCoord))
                {
                    chunksToModify.Add(neighborCoord);
                }
                else
                {
                    // Density update already queued above, just request immediate load
                    // Request immediate load
                    if (TerrainAnalysisCache.TryGetAnalysis(neighborCoord, out var analysis) && analysis.IsSolid)
                    {
                        LoadChunkImmediately(neighborCoord);
                    }
                    else
                    {
                        RequestImmediateChunkLoad(neighborCoord);
                    }
                }
            }

            // Force complete jobs on all affected chunks
            foreach (var neighborCoord in chunksToModify)
            {
                if (chunks.TryGetValue(neighborCoord, out Chunk neighborChunk))
                {
                    neighborChunk.CompleteAllJobs();
                    neighborChunk.EnsureDataInitialized();
                }
            }

            // Process density updates for all identified chunks
            List<Vector3Int> chunksToSave = new List<Vector3Int>();

            foreach (var neighborCoord in chunksToModify)
            {
                var neighborState = ChunkStateManager.Instance.GetChunkState(neighborCoord);
                if (chunks.TryGetValue(neighborCoord, out Chunk neighborChunk) && 
                    (neighborState.Status == ChunkConfigurations.ChunkStatus.Loaded || 
                    neighborState.Status == ChunkConfigurations.ChunkStatus.Modified))
                {
                    neighborChunk.CompleteAllJobs();
                    neighborChunk.EnsureDataInitialized();
                    
                    bool forceFalloff = chunkForceFalloffFlags.TryGetValue(neighborCoord, out var flag) && flag;
                    bool densityChanged = ApplyDensityUpdate(neighborChunk, worldPos, false, forceFalloff);
                    
                    if (densityChanged)
                    {
                        // Always transition to Modified state if density actually changed
                        if (neighborState.Status != ChunkConfigurations.ChunkStatus.Modified)
                        {
                            ChunkStateManager.Instance.TryChangeState(
                                neighborCoord,
                                ChunkConfigurations.ChunkStatus.Modified,
                                ChunkConfigurations.ChunkStateFlags.Active
                            );
                        }
                        
                        // Add to chunks that need saving
                        chunksToSave.Add(neighborCoord);
                        
                        // CRITICAL FIX: Always invalidate the analysis after confirmed density changes
                        TerrainAnalysisCache.InvalidateAnalysis(neighborCoord);
                        ClearForcedChunkFlag(neighborCoord, "Density applied via HandleVoxelDestruction");
                    }
                }
                else
                {
                    QueueDensityUpdate(neighborCoord, worldPos);
                    
                    // Only request load if not already loading
                    state = ChunkStateManager.Instance.GetChunkState(neighborCoord);
                    if (state.Status != ChunkConfigurations.ChunkStatus.Loading)
                    {
                        RequestImmediateChunkLoad(neighborCoord);
                    }
                }
            }

            // Process mesh updates after all density updates
            foreach (var neighborCoord in chunksToModify)
            {
                if (chunks.TryGetValue(neighborCoord, out Chunk neighborChunk))
                {
                    // Generate the mesh
                    neighborChunk.Generate(log: false, fullMesh: false, quickCheck: false);
                }
            }
            
            // Save all modified chunks explicitly
            foreach (var coordToSave in chunksToSave)
            {
                if (chunks.TryGetValue(coordToSave, out Chunk chunkToSave) && 
                    chunkToSave.GetChunkData() != null)
                {
                    // Force save after modification
                    chunkToSave.GetChunkData().SaveData();
                    
                    // CRITICAL FIX: After saving, ensure the chunk is properly tracked as modified
                    if (TerrainAnalysisCache.TryGetAnalysis(coordToSave, out var cacheEntry) && cacheEntry.IsSolid)
                    {
                        // Create new entry marking it as modified with flags set correctly
                        TerrainAnalysisCache.SaveAnalysis(coordToSave, false, false, true);
                        Debug.Log($"Updated terrain analysis for modified solid chunk {coordToSave} - explicitly marked as non-solid and modified");
                    }
                }
            }
        }
        catch (Exception ex) {
            Debug.LogError($"Error in HandleVoxelDestruction: {ex.Message}\n{ex.StackTrace}");
        }
        finally {
            currentlyProcessingChunks.Remove(chunkCoord);
        }
    }

    private bool LoadChunkImmediately(Vector3Int chunkCoord)
    {
        Debug.Log($"*** DIRECT CHUNK LOADING: {chunkCoord} ***");
        
        // Skip if already loaded
        if (chunks.ContainsKey(chunkCoord))
        {
            Debug.Log($"Chunk {chunkCoord} already loaded");
            return true;
        }
        
        // CRITICAL FIX: Allow loading quarantined chunks if they have pending updates
        // This matches RequestImmediateChunkLoad behavior and enables recovery
        bool isQuarantined = ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord);
        bool hasPendingUpdates = HasPendingUpdates(chunkCoord);
        
        if (isQuarantined && !hasPendingUpdates)
        {
            Debug.LogWarning($"Can't load quarantined chunk {chunkCoord} - no pending updates");
            return false;
        }
        
        if (isQuarantined && hasPendingUpdates)
        {
            Debug.LogWarning($"Allowing load request for QUARANTINED chunk {chunkCoord} because it has pending updates");
        }

        try
        {
            // Get current state first
            var currentState = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            
            // CRITICAL FIX: Handle stuck Loading/Error states by attempting recovery
            if (currentState.Status == ChunkConfigurations.ChunkStatus.Loading)
            {
                Debug.LogWarning($"Chunk {chunkCoord} stuck in Loading state - attempting recovery");
                if (AttemptChunkRecovery(chunkCoord))
                {
                    // Recovery succeeded, state should now be None - continue with load
                    currentState = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                }
                else
                {
                    Debug.LogError($"Failed to recover chunk {chunkCoord} from Loading state");
                    return false;
                }
            }
            else if (currentState.Status == ChunkConfigurations.ChunkStatus.Error)
            {
                Debug.LogWarning($"Chunk {chunkCoord} in Error state - attempting recovery");
                if (AttemptChunkRecovery(chunkCoord))
                {
                    // Recovery succeeded, state should now be None - continue with load
                    currentState = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                }
                else
                {
                    Debug.LogError($"Failed to recover chunk {chunkCoord} from Error state");
                    return false;
                }
            }
            
            // CRITICAL FIX: Handle chunks in Unloading state
            // If chunk is unloading, we need to wait for it to finish or cancel the unload
            if (currentState.Status == ChunkConfigurations.ChunkStatus.Unloading)
            {
                Debug.LogWarning($"Chunk {chunkCoord} is currently UNLOADING - " +
                    $"HasPendingUpdates: {hasPendingUpdates}, IsMarkedForMod: {modifiedSolidChunks.Contains(chunkCoord)}");
                LogChunkTrace(chunkCoord, $"LoadChunkImmediately: Chunk is UNLOADING");
                
                // If chunk has pending updates or is marked for modification, cancel the unload
                // by forcing state to Unloaded so it can be reloaded
                if (hasPendingUpdates || modifiedSolidChunks.Contains(chunkCoord))
                {
                    Debug.LogWarning($"Cancelling unload for chunk {chunkCoord} due to pending updates/modifications");
                    LogChunkTrace(chunkCoord, $"LoadChunkImmediately: Cancelling unload due to pending updates");
                    
                    // Try to transition Unloading -> Unloaded -> None so we can reload
                    if (ChunkStateManager.Instance.TryChangeState(
                        chunkCoord,
                        ChunkConfigurations.ChunkStatus.Unloaded,
                        ChunkConfigurations.ChunkStateFlags.None))
                    {
                        currentState = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                        Debug.Log($"Successfully cancelled unload for chunk {chunkCoord}, new state: {currentState.Status}");
                        LogChunkTrace(chunkCoord, $"LoadChunkImmediately: Successfully cancelled unload");
                    }
                    else
                    {
                        Debug.LogError($"Failed to cancel unload for chunk {chunkCoord}");
                        LogChunkTrace(chunkCoord, $"LoadChunkImmediately: FAILED to cancel unload");
                        return false;
                    }
                }
                else
                {
                    // No pending updates, let unload complete
                    Debug.Log($"Chunk {chunkCoord} is unloading with no pending updates - cannot load immediately");
                    LogChunkTrace(chunkCoord, $"LoadChunkImmediately: Waiting for unload to complete");
                    return false;
                }
            }
            
            // CRITICAL FIX: Don't try to force transition if it would be invalid
            // Instead, follow the proper state machine flow
            if (currentState.Status != ChunkConfigurations.ChunkStatus.None && 
                currentState.Status != ChunkConfigurations.ChunkStatus.Unloaded)
            {
                Debug.LogWarning($"Chunk {chunkCoord} in invalid state for loading: {currentState.Status}");
                return false;
            }
            
            // Change to Loading state
            if (!ChunkStateManager.Instance.TryChangeState(
                chunkCoord,
                ChunkConfigurations.ChunkStatus.Loading,
                ChunkConfigurations.ChunkStateFlags.None))
            {
                Debug.LogWarning($"Failed to change state to Loading for chunk {chunkCoord} from {currentState.Status}");
                return false;
            }
            
            // Track when chunk enters Loading state for stuck chunk detection
            chunkLoadingStartTime[chunkCoord] = Time.time;

            // Get a chunk from pool
            Chunk chunkObject = ChunkPoolManager.Instance.GetChunk();
            if (chunkObject == null)
            {
                Debug.LogError($"Failed to get chunk from pool for {chunkCoord} - pool exhausted. " +
                    $"Falling back to queued load instead of immediate load.");
                LogChunkTrace(chunkCoord, $"LoadChunkImmediately: Pool exhausted, falling back to queued load");
                
                // CRITICAL FIX: If pool is exhausted, fall back to queued load instead of failing
                // This ensures the chunk will still load when pool becomes available
                if (ChunkStateManager.Instance.TryChangeState(
                    chunkCoord,
                    ChunkConfigurations.ChunkStatus.None,
                    ChunkConfigurations.ChunkStateFlags.None))
                {
                    try
                    {
                        operationsQueue.QueueChunkForLoad(chunkCoord, immediate: true, quickCheck: false);
                        Debug.Log($"Successfully queued chunk {chunkCoord} for load (pool exhausted, using queue)");
                        LogChunkTrace(chunkCoord, $"LoadChunkImmediately: Queued for load (pool exhausted)");
                        return true; // Return true because we successfully queued it
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Failed to queue chunk {chunkCoord} after pool exhaustion: {e.Message}");
                        LogChunkTrace(chunkCoord, $"LoadChunkImmediately: ERROR - Failed to queue after pool exhaustion: {e.Message}");
                        return false;
                    }
                }
                else
                {
                    Debug.LogError($"Failed to reset state for chunk {chunkCoord} after pool exhaustion");
                    return false;
                }
            }

            // Position and initialize
            Vector3 chunkPosition = Coord.GetWorldPosition(
                chunkCoord,
                Vector3Int.zero,
                chunkSize,
                voxelSize);
            
            // Important: For solid chunks being modified, disable quick check!
            bool disableQuickCheck = false;
            if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis.IsSolid)
            {
                disableQuickCheck = true;
                Debug.Log($"SOLID CHUNK: Disabling quickCheck for {chunkCoord}");
            }
            
            chunkObject.transform.position = chunkPosition;
            chunkObject.gameObject.SetActive(true);
            
            chunkObject.Initialize(
                chunkSize,
                surfaceLevel,
                voxelSize,
                chunkPosition,
                quickCheck: !disableQuickCheck);

            // Register with the World
            RegisterChunk(chunkCoord, chunkObject);
            
            Debug.Log($"Successfully loaded chunk {chunkCoord} directly");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load chunk {chunkCoord} directly: {e.Message}\n{e.StackTrace}");
            return false;
        }
    }

    private bool ForceLoadChunk(Vector3Int chunkCoord)
    {
        // Skip if already loaded
        if (chunks.ContainsKey(chunkCoord))
        {
            return true;
        }
        
        // Skip if quarantined
        if (ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord))
        {
            Debug.LogWarning($"Skipping force load for quarantined chunk {chunkCoord}");
            return false;
        }

        // First, try to change the state to ensure we're in a valid state for loading
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
        if (state.Status != ChunkConfigurations.ChunkStatus.None && 
            state.Status != ChunkConfigurations.ChunkStatus.Unloaded)
        {
            // Force a state change to None if possible
            if (!ChunkStateManager.Instance.TryChangeState(chunkCoord, 
                ChunkConfigurations.ChunkStatus.None, 
                ChunkConfigurations.ChunkStateFlags.None))
            {
                Debug.LogWarning($"Can't force state change for chunk {chunkCoord}, current state: {state.Status}");
                return false;
            }
        }

        try
        {
            // Change to Loading state
            if (!ChunkStateManager.Instance.TryChangeState(chunkCoord, 
                ChunkConfigurations.ChunkStatus.Loading, 
                ChunkConfigurations.ChunkStateFlags.None))
            {
                Debug.LogWarning($"Failed to change state to Loading for chunk {chunkCoord}");
                return false;
            }
            
            // Track when chunk enters Loading state for stuck chunk detection
            chunkLoadingStartTime[chunkCoord] = Time.time;

            // Get a chunk from pool
            Chunk chunkObject = ChunkPoolManager.Instance.GetChunk();
            if (chunkObject == null)
            {
                Debug.LogError($"Failed to get chunk from pool for {chunkCoord}");
                return false;
            }

            // Position and initialize - disable quickCheck to ensure we generate properly
            Vector3 chunkPosition = GetChunkWorldPosition(chunkCoord);
            
            chunkObject.transform.position = chunkPosition;
            chunkObject.gameObject.SetActive(true);
            
            Debug.Log($"Force loading chunk at {chunkCoord}, position: {chunkPosition}");
            
            chunkObject.Initialize(
                chunkSize,
                surfaceLevel,
                voxelSize,
                chunkPosition,
                quickCheck: false);  // No quick check for force loaded chunks

            // Register with the World
            RegisterChunk(chunkCoord, chunkObject);
            
            // Mark chunk as loaded
            OnChunkLoadSucceeded(chunkCoord);
            
            Debug.Log($"Successfully force loaded chunk {chunkCoord}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to force load chunk {chunkCoord}: {e.Message}");
            OnChunkLoadFailed(chunkCoord, $"Force load failed: {e.Message}");
            return false;
        }
    }

    private void PreloadSolidNeighbors(Vector3 worldPos, float radius)
    {
        // Calculate which chunks might be affected by this operation
        Vector3Int centerChunk = Coord.WorldToChunkCoord(worldPos, chunkSize, voxelSize);
        
        // First check immediately adjacent neighbors (6-connected)
        Vector3Int[] directNeighbors = new Vector3Int[]
        {
            centerChunk + new Vector3Int(1, 0, 0),
            centerChunk + new Vector3Int(-1, 0, 0),
            centerChunk + new Vector3Int(0, 1, 0),
            centerChunk + new Vector3Int(0, -1, 0),
            centerChunk + new Vector3Int(0, 0, 1),
            centerChunk + new Vector3Int(0, 0, -1)
        };
        
        // Process immediate neighbors first - these are most critical
        foreach (var neighborCoord in directNeighbors)
        {
            // If it's a solid chunk, we need to prepare it for modification
            if (TerrainAnalysisCache.TryGetAnalysis(neighborCoord, out var analysis) && analysis.IsSolid)
            {
                Debug.Log($"Found solid neighbor at {neighborCoord}, marking for modification");
                MarkSolidChunkForModification(neighborCoord);
                
                // Always trigger load immediately for solid chunks
                if (!chunks.ContainsKey(neighborCoord))
                {
                    Debug.Log($"Requesting immediate load for solid neighbor {neighborCoord}");
                    operationsQueue.QueueChunkForLoad(neighborCoord, immediate: true, quickCheck: false);
                }
            }
        }
        
        // Then check diagonal neighbors - this help with larger modifications
        for (int x = -1; x <= 1; x++)
        for (int z = -1; z <= 1; z++)
        {
            // Skip the center and direct neighbors (already processed)
            if ((x == 0 && z == 0) || 
                (x == 0 && z != 0) || 
                (x != 0 && z == 0))
                continue;
                
            Vector3Int diagonalNeighbor = centerChunk + new Vector3Int(x, 0, z);
            
            // Check if it's a solid chunk
            if (TerrainAnalysisCache.TryGetAnalysis(diagonalNeighbor, out var analysis) && analysis.IsSolid)
            {
                Debug.Log($"Found solid diagonal neighbor at {diagonalNeighbor}, marking for modification");
                MarkSolidChunkForModification(diagonalNeighbor);
                
                // Queue with slightly lower priority than immediate neighbors
                if (!chunks.ContainsKey(diagonalNeighbor))
                {
                    operationsQueue.QueueChunkForLoad(diagonalNeighbor, immediate: true, quickCheck: false);
                }
            }
        }
    }

    private bool ApplyDensityUpdate(Chunk chunk, Vector3 worldPos, bool isAdding, bool forceBoundaryFalloff = false)
    {
        // Check if the chunk is a solid chunk that was just loaded
        Vector3Int chunkCoord = chunk.GetChunkData().ChunkCoordinate;
        bool wasSolid = false;
        
        if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis.IsSolid)
        {
            wasSolid = true;
            Debug.Log($"Processing solid chunk {chunkCoord} for density update - SPECIAL HANDLING");
        }
        
        // Track if any density values changed
        bool anyDensityChanged = false;
        
        // The chunk origin in world space is needed for coordinate conversions
        Vector3 chunkOrigin = chunk.transform.position;
        Vector3 expectedOrigin = GetChunkWorldPosition(chunkCoord);
        
        // DIAGNOSTIC: Check if chunk's actual position matches expected position
        if (chunkOrigin != expectedOrigin)
        {
            Debug.LogError($"[ApplyDensityUpdate] POSITION MISMATCH for chunk {chunkCoord}! " +
                $"GameObject position: {chunkOrigin}, Expected: {expectedOrigin}, Diff: {chunkOrigin - expectedOrigin}");
        }
        
        // DIAGNOSTIC: Log detailed information for traced chunks OR problematic coordinates
        if (ShouldLogChunkDiagnostics(chunkCoord) || IsProblematicCoordinate(chunkCoord))
        {
            var chunkData = chunk.GetChunkData();
            string prefix = IsProblematicCoordinate(chunkCoord) ? "[COORD_5_DEBUG]" : $"[MOD_DIAG:{chunkCoord}]";
            Debug.Log($"{prefix} ApplyDensityUpdate called - " +
                $"worldPos: {worldPos}, " +
                $"chunkOrigin: {chunkOrigin}, " +
                $"isAdding: {isAdding}, " +
                $"forceBoundaryFalloff: {forceBoundaryFalloff}, " +
                $"HasSavedData: {chunkData.HasSavedData}, " +
                $"IsSolid: {chunkData.IsSolidChunk}, " +
                $"IsEmpty: {chunkData.IsEmptyChunk}, " +
                $"DensityPointsCreated: {chunkData.DensityPoints.IsCreated}");
        }
        
        // Calculate influencing radius based on voxel size
        // CRITICAL FIX: Use the same radius calculation as GetAffectedChunks to ensure consistency
        // GetAffectedChunks uses: radius = voxelSize * (Config.densityInfluenceRadius + 1f)
        float radius = voxelSize * (Config.densityInfluenceRadius + 1f);
        
        // For solid chunks, use a larger radius to ensure we modify enough of the chunk
        // This matches GetAffectedChunks which uses radius * 1.5f for solid chunks
        if (wasSolid)
        {
            radius *= 1.5f;
            Debug.Log($"Using larger radius for solid chunk: {radius}");
        }
        
        // Determine which region of the chunk to update based on the world position
        int densityRange = Mathf.CeilToInt(radius / voxelSize);
        int totalPointsPerAxis = chunk.GetChunkData().TotalPointsPerAxis;
        
        // Convert world mining position to LOCAL density coordinates in this chunk
        Vector3Int localMiningPos = Coord.WorldToDensityCoord(worldPos, chunkOrigin, voxelSize);
        
        // DIAGNOSTIC: Always log for problematic coordinates
        if (IsProblematicCoordinate(chunkCoord))
        {
            Debug.Log($"[COORD_5_DEBUG] ApplyDensityUpdate conversion - Mining world pos: {worldPos}, chunk origin: {chunkOrigin}, local density pos: {localMiningPos}, totalPointsPerAxis: {totalPointsPerAxis}");
        }
        else
        {
            Debug.Log($"[ApplyDensityUpdate] Mining world pos: {worldPos}, chunk origin: {chunkOrigin}, local density pos in chunk {chunkCoord}: {localMiningPos}, totalPointsPerAxis: {totalPointsPerAxis}");
        }
        
        // Create bounds for our update region
        int minX, maxX, minY, maxY, minZ, maxZ;
        
        if (wasSolid)
        {
            // For solid chunks, determine which side of the chunk we're mining from
            bool onMinXBoundary = localMiningPos.x <= densityRange;
            bool onMaxXBoundary = localMiningPos.x >= totalPointsPerAxis - densityRange;
            bool onMinZBoundary = localMiningPos.z <= densityRange;
            bool onMaxZBoundary = localMiningPos.z >= totalPointsPerAxis - densityRange;
            
            // Adjust the update region based on which boundaries we're near
            if (onMinXBoundary)
            {
                minX = 0;
                maxX = densityRange * 2;
                Debug.Log($"Mining near MIN X boundary of solid chunk {chunkCoord}");
            }
            else if (onMaxXBoundary)
            {
                minX = totalPointsPerAxis - densityRange * 2;
                maxX = totalPointsPerAxis - 1;
                Debug.Log($"Mining near MAX X boundary of solid chunk {chunkCoord}");
            }
            else
            {
                // Not near an X boundary, use a region around the mining position
                minX = Mathf.Max(0, localMiningPos.x - densityRange);
                maxX = Mathf.Min(totalPointsPerAxis - 1, localMiningPos.x + densityRange);
            }
            
            if (onMinZBoundary)
            {
                minZ = 0;
                maxZ = densityRange * 2;
                Debug.Log($"Mining near MIN Z boundary of solid chunk {chunkCoord}");
            }
            else if (onMaxZBoundary)
            {
                minZ = totalPointsPerAxis - densityRange * 2;
                maxZ = totalPointsPerAxis - 1;
                Debug.Log($"Mining near MAX Z boundary of solid chunk {chunkCoord}");
            }
            else
            {
                // Not near a Z boundary, use a region around the mining position
                minZ = Mathf.Max(0, localMiningPos.z - densityRange);
                maxZ = Mathf.Min(totalPointsPerAxis - 1, localMiningPos.z + densityRange);
            }
            
            // Y direction is simpler - just use a range around the mining position
            minY = Mathf.Max(0, localMiningPos.y - densityRange);
            maxY = Mathf.Min(totalPointsPerAxis - 1, localMiningPos.y + densityRange);
            
            Debug.Log($"Updating density region in solid chunk {chunkCoord}: X({minX}-{maxX}), Y({minY}-{maxY}), Z({minZ}-{maxZ})");
        }
        else
        {
            // Standard approach for normal chunks - use a region around the mining position
            // CRITICAL FIX: Handle boundary cases where localMiningPos is negative (outside chunk bounds)
            // For boundary mining, we need to ensure we update density points at the chunk edges
            minX = Mathf.Max(0, localMiningPos.x - densityRange);
            maxX = Mathf.Min(totalPointsPerAxis - 1, localMiningPos.x + densityRange);
            minY = Mathf.Max(0, localMiningPos.y - densityRange);
            maxY = Mathf.Min(totalPointsPerAxis - 1, localMiningPos.y + densityRange);
            minZ = Mathf.Max(0, localMiningPos.z - densityRange);
            maxZ = Mathf.Min(totalPointsPerAxis - 1, localMiningPos.z + densityRange);
            
            // CRITICAL FIX: If mining position is outside chunk bounds (negative or beyond), ensure we still update boundary points
            // This handles cases where mining happens at chunk seams or far outside the chunk
            // The key is to always create a valid update region at the nearest chunk boundary
            if (localMiningPos.x < 0)
            {
                // Mining from negative X side (or far outside) - ensure we update the X=0 boundary
                minX = 0;
                maxX = Mathf.Min(totalPointsPerAxis - 1, densityRange);
                Debug.Log($"[ApplyDensityUpdate] Mining from negative X side (localMiningPos.x={localMiningPos.x}), updating boundary region X(0-{maxX})");
            }
            else if (localMiningPos.x >= totalPointsPerAxis)
            {
                // Mining from positive X side (or far outside) - ensure we update the X=totalPointsPerAxis-1 boundary
                minX = Mathf.Max(0, totalPointsPerAxis - 1 - densityRange);
                maxX = totalPointsPerAxis - 1;
                Debug.Log($"[ApplyDensityUpdate] Mining from positive X side (localMiningPos.x={localMiningPos.x}), updating boundary region X({minX}-{maxX})");
            }
            // else: localMiningPos.x is within bounds, use the calculated minX/maxX from above
            
            if (localMiningPos.y < 0)
            {
                minY = 0;
                maxY = Mathf.Min(totalPointsPerAxis - 1, densityRange);
                Debug.Log($"[ApplyDensityUpdate] Mining from negative Y side (localMiningPos.y={localMiningPos.y}), updating boundary region Y(0-{maxY})");
            }
            else if (localMiningPos.y >= totalPointsPerAxis)
            {
                minY = Mathf.Max(0, totalPointsPerAxis - 1 - densityRange);
                maxY = totalPointsPerAxis - 1;
                Debug.Log($"[ApplyDensityUpdate] Mining from positive Y side (localMiningPos.y={localMiningPos.y}), updating boundary region Y({minY}-{maxY})");
            }
            // else: localMiningPos.y is within bounds, use the calculated minY/maxY from above
            
            if (localMiningPos.z < 0)
            {
                minZ = 0;
                maxZ = Mathf.Min(totalPointsPerAxis - 1, densityRange);
                Debug.Log($"[ApplyDensityUpdate] Mining from negative Z side (localMiningPos.z={localMiningPos.z}), updating boundary region Z(0-{maxZ})");
            }
            else if (localMiningPos.z >= totalPointsPerAxis)
            {
                minZ = Mathf.Max(0, totalPointsPerAxis - 1 - densityRange);
                maxZ = totalPointsPerAxis - 1;
                Debug.Log($"[ApplyDensityUpdate] Mining from positive Z side (localMiningPos.z={localMiningPos.z}), updating boundary region Z({minZ}-{maxZ})");
            }
            // else: localMiningPos.z is within bounds, use the calculated minZ/maxZ from above
        }
        
        // DEBUG: Log update region details
        int totalPointsInRegion = (maxX - minX + 1) * (maxY - minY + 1) * (maxZ - minZ + 1);
        bool isEmptyRegion = (minX > maxX) || (minY > maxY) || (minZ > maxZ);
        
        Debug.Log($"[ApplyDensityUpdate] Chunk {chunkCoord} - Update region: X({minX}-{maxX}), Y({minY}-{maxY}), Z({minZ}-{maxZ}), " +
            $"Total points: {totalPointsInRegion}, Empty: {isEmptyRegion}, " +
            $"WorldPos: {worldPos}, LocalMiningPos: {localMiningPos}, Radius: {radius}, DensityRange: {densityRange}, " +
            $"WasSolid: {wasSolid}, ForceBoundaryFalloff: {forceBoundaryFalloff}, TotalPointsPerAxis: {totalPointsPerAxis}");
        LogChunkTrace(chunkCoord, $"ApplyDensityUpdate: Update region X({minX}-{maxX}) Y({minY}-{maxY}) Z({minZ}-{maxZ}), Total points: {totalPointsInRegion}, Empty: {isEmptyRegion}");
        
        if (isEmptyRegion)
        {
            Debug.LogWarning($"[ApplyDensityUpdate] Chunk {chunkCoord} has EMPTY update region! This will cause no density changes. " +
                $"WorldPos: {worldPos}, ChunkOrigin: {chunkOrigin}, LocalMiningPos: {localMiningPos}");
            LogChunkTrace(chunkCoord, $"ApplyDensityUpdate: WARNING - Empty update region!");
            return false;
        }
        
        // Track statistics for debugging
        int pointsProcessed = 0;
        int pointsPassedFalloff = 0;
        int pointsPassedThreshold = 0;
        int pointsSetSuccessfully = 0;
        int pointsSkippedInvalid = 0;
        int pointsSkippedFalloff = 0;
        int pointsSkippedThreshold = 0;
        int pointsFailedSet = 0;
        
        // Sample values for first few points (for debugging)
        List<string> samplePoints = new List<string>();
        int sampleCount = 0;
        const int maxSamples = 5;
        
        // Safely iterate through the region and update density values
        for (int x = minX; x <= maxX; x++)
        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        {
            Vector3Int densityPos = new Vector3Int(x, y, z);
            pointsProcessed++;
            
            // Skip invalid positions
            if (!Coord.IsDensityPositionValid(densityPos, totalPointsPerAxis))
            {
                pointsSkippedInvalid++;
                continue;
            }
            
            // Calculate world position of this density point
            Vector3 pointWorldPos = chunkOrigin + new Vector3(x, y, z) * voxelSize;
            
            // Calculate distance from the mining point for falloff
            float distance = Vector3.Distance(worldPos, pointWorldPos);
            float falloff = CalculateDensityFalloff(distance, radius);
            if (forceBoundaryFalloff && falloff < 1f)
            {
                falloff = 1f;
            }
            
            // Only apply significant changes
            if (falloff > Config.minDensityChangeThreshold)
            {
                pointsPassedFalloff++;
                try
                {
                    // Get current density at this position
                    float oldDensity = chunk.GetDensityAtPosition(densityPos);
                    float newDensity;
                    
                    if (wasSolid)
                    {
                        // For solid chunks, use a more aggressive approach with higher target density
                        // This ensures we create proper holes in solid chunks
                        newDensity = Mathf.Lerp(oldDensity, surfaceLevel + 2.0f, falloff * 1.5f);
                        
                        // Ensure minimum change for solid chunks to prevent tiny dents
                        if (Math.Abs(newDensity - oldDensity) < 0.5f)
                        {
                            newDensity = surfaceLevel + 1.0f;
                        }
                    }
                    else
                    {
                        // Standard approach for normal chunks
                        float targetDensity = isAdding ? surfaceLevel - 1.5f : surfaceLevel + 1.5f;
                        newDensity = Mathf.Lerp(oldDensity, targetDensity, falloff);
                    }
                    
                    // Apply the density change
                    if (Math.Abs(newDensity - oldDensity) > Config.minDensityChangeThreshold)
                    {
                        pointsPassedThreshold++;
                        
                        // Collect sample data for first few points
                        if (sampleCount < maxSamples)
                        {
                            samplePoints.Add($"Pos({x},{y},{z}) dist={distance:F2} falloff={falloff:F4} old={oldDensity:F3} new={newDensity:F3} diff={Math.Abs(newDensity - oldDensity):F3}");
                            sampleCount++;
                        }
                        
                        bool success = chunk.TrySetDensityPoint(densityPos, newDensity);
                        if (success)
                        {
                            pointsSetSuccessfully++;
                            anyDensityChanged = true;
                        }
                        else
                        {
                            pointsFailedSet++;
                            if (sampleCount < maxSamples)
                            {
                                Debug.LogWarning($"[ApplyDensityUpdate] TrySetDensityPoint FAILED for chunk {chunkCoord} at {densityPos} - oldDensity: {oldDensity:F3}, newDensity: {newDensity:F3}");
                            }
                        }
                    }
                    else
                    {
                        pointsSkippedThreshold++;
                        if (sampleCount < maxSamples)
                        {
                            samplePoints.Add($"Pos({x},{y},{z}) SKIPPED - density change too small: old={oldDensity:F3} new={newDensity:F3} diff={Math.Abs(newDensity - oldDensity):F3} threshold={Config.minDensityChangeThreshold}");
                            sampleCount++;
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error updating density at {densityPos} in chunk {chunkCoord}: {e.Message}");
                }
            }
            else
            {
                pointsSkippedFalloff++;
                if (sampleCount < maxSamples && pointsSkippedFalloff <= 2)
                {
                    samplePoints.Add($"Pos({x},{y},{z}) SKIPPED - falloff too small: distance={distance:F2} radius={radius:F2} falloff={falloff:F4} threshold={Config.minDensityChangeThreshold}");
                    sampleCount++;
                }
            }
        }
        
        // DEBUG: Log statistics
        Debug.Log($"[ApplyDensityUpdate] Chunk {chunkCoord} - Statistics: " +
            $"Processed: {pointsProcessed}, " +
            $"PassedFalloff: {pointsPassedFalloff}, " +
            $"PassedThreshold: {pointsPassedThreshold}, " +
            $"SetSuccessfully: {pointsSetSuccessfully}, " +
            $"SkippedInvalid: {pointsSkippedInvalid}, " +
            $"SkippedFalloff: {pointsSkippedFalloff}, " +
            $"SkippedThreshold: {pointsSkippedThreshold}, " +
            $"FailedSet: {pointsFailedSet}, " +
            $"AnyDensityChanged: {anyDensityChanged}");
        LogChunkTrace(chunkCoord, $"ApplyDensityUpdate: Processed={pointsProcessed}, PassedFalloff={pointsPassedFalloff}, PassedThreshold={pointsPassedThreshold}, SetSuccessfully={pointsSetSuccessfully}, AnyDensityChanged={anyDensityChanged}");
        LogChunkModificationSummary(
            chunkCoord,
            worldPos,
            chunkOrigin,
            radius,
            localMiningPos,
            wasSolid,
            anyDensityChanged,
            pointsProcessed,
            pointsPassedFalloff,
            pointsPassedThreshold,
            pointsSetSuccessfully,
            pointsSkippedFalloff,
            forceBoundaryFalloff);
        
        // Log sample points
        if (samplePoints.Count > 0)
        {
            Debug.Log($"[ApplyDensityUpdate] Chunk {chunkCoord} - Sample points:\n" + string.Join("\n", samplePoints));
        }
        
        if (!anyDensityChanged && pointsProcessed > 0)
        {
            Debug.LogWarning($"[ApplyDensityUpdate] Chunk {chunkCoord} - NO density changes applied despite processing {pointsProcessed} points! " +
                $"This suggests the update region might be wrong, falloff too small, or density already at target value.");
            LogChunkTrace(chunkCoord, $"ApplyDensityUpdate: WARNING - No density changes applied!");
        }
        
        // If we've modified the density field, update voxel states to match
        if (anyDensityChanged && wasSolid)
        {
            UpdateVoxelsFromDensity(chunk, minX, minY, minZ, maxX, maxY, maxZ);
        }
        
        // If density changed, ensure this chunk gets remeshed
        if (anyDensityChanged)
        {
            chunk.isMeshUpdateQueued = true;
            chunksNeedingMeshUpdate.Add(chunk);
            
            // CRITICAL FIX: If we modified a solid chunk, explicitly update its terrain analysis
            if (wasSolid)
            {
                Debug.Log($"Successfully modified solid chunk {chunkCoord}, queued for remeshing");
                // Mark as modified to ensure it gets saved and loaded properly next time
                TerrainAnalysisCache.SaveAnalysis(chunkCoord, false, false, true);
                // Add to modified solid chunks tracking
                MarkSolidChunkForModification(chunkCoord);
            }
        }
        
        return anyDensityChanged;
    }

    private void UpdateVoxelsFromDensity(Chunk chunk, int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
    {
        // Convert density point range to voxel range (density points are at corners of voxels)
        int minVoxelX = Mathf.Max(0, minX - 1);
        int minVoxelY = Mathf.Max(0, minY - 1);
        int minVoxelZ = Mathf.Max(0, minZ - 1);
        
        int maxVoxelX = Mathf.Min(chunk.GetChunkData().TotalPointsPerAxis - 2, maxX - 1);
        int maxVoxelY = Mathf.Min(chunk.GetChunkData().TotalPointsPerAxis - 2, maxY - 1);
        int maxVoxelZ = Mathf.Min(chunk.GetChunkData().TotalPointsPerAxis - 2, maxZ - 1);

        int chunkSize = chunk.GetChunkData().TotalPointsPerAxis - 1;
        float surfaceLevel = chunk.GetChunkData().SurfaceLevel;
        Vector3Int chunkCoord = chunk.GetChunkData().ChunkCoordinate;
        
        Debug.Log($"Updating voxels in chunk {chunkCoord} from region: " +
                $"X({minVoxelX}-{maxVoxelX}), Y({minVoxelY}-{maxVoxelY}), Z({minVoxelZ}-{maxVoxelZ})");
        
        int updatedCount = 0;
        
        // Update voxel states based on surrounding density points
        for (int x = minVoxelX; x <= maxVoxelX; x++)
        for (int y = minVoxelY; y <= maxVoxelY; y++)
        for (int z = minVoxelZ; z <= maxVoxelZ; z++)
        {
            Vector3Int voxelPos = new Vector3Int(x, y, z);
            Vector3Int densityPos = Coord.VoxelToDensityCoord(voxelPos);
            
            // Check if any of the 8 corners have density < surfaceLevel
            bool shouldBeActive = false;
            for (int dx = 0; dx <= 1; dx++)
            for (int dy = 0; dy <= 1; dy++)
            for (int dz = 0; dz <= 1; dz++)
            {
                Vector3Int cornerPos = densityPos + new Vector3Int(dx, dy, dz);
                
                // Skip if out of bounds
                if (!Coord.IsDensityPositionValid(cornerPos, chunk.GetChunkData().TotalPointsPerAxis))
                    continue;
                    
                float density = chunk.GetDensityAtPosition(cornerPos);
                if (density < surfaceLevel)
                {
                    shouldBeActive = true;
                    break;
                }
            }
            
            // Update voxel state if needed
            int voxelIndex = Coord.GetVoxelIndex(voxelPos, chunkSize);
            Chunk.Voxel currentVoxel = chunk.GetVoxel(voxelPos);
            
            bool isCurrentlyActive = currentVoxel.isActive == Chunk.VOXEL_ACTIVE;
            
            if (shouldBeActive != isCurrentlyActive)
            {
                chunk.SetVoxelDirect(
                    voxelPos,
                    shouldBeActive ? Chunk.VOXEL_ACTIVE : Chunk.VOXEL_INACTIVE,
                    shouldBeActive ? 3 : 0
                );
                updatedCount++;
            }
        }
        
        Debug.Log($"Updated {updatedCount} voxels in chunk {chunkCoord} to match density field");
    }

    private float CalculateDensityFalloff(float distance, float radius)
    {
        if (distance >= radius) return 0f;
        
        float normalizedDistance = distance / radius;
        // Use a more aggressive falloff curve
        float smoothFalloff = 1f - (normalizedDistance * normalizedDistance);
        
        // Add a sharp cutoff for small values to prevent endless updates
        return smoothFalloff < Config.densityFalloffCutoff ? 0f : smoothFalloff;
    }

    #endregion

    #region Coordinate Transforms
    private HashSet<Vector3Int> GetAffectedChunks(Vector3 worldPos, float radius)
    {
        HashSet<Vector3Int> affectedChunks = new HashSet<Vector3Int>();
        
        // Add center chunk first
        Vector3Int centerChunk = Coord.WorldToChunkCoord(worldPos, chunkSize, voxelSize);
        affectedChunks.Add(centerChunk);
        
        // CRITICAL FIX: Calculate bounds with extra padding to ensure we catch edge cases
        // Increase padding to be more reliable at catching neighboring chunks
        float padRadius = radius * 1.5f + voxelSize * 2f;
        
        Vector3 minBound = worldPos - Vector3.one * padRadius;
        Vector3 maxBound = worldPos + Vector3.one * padRadius;
        
        Vector3Int minChunk = Coord.WorldToChunkCoord(minBound, chunkSize, voxelSize);
        Vector3Int maxChunk = Coord.WorldToChunkCoord(maxBound, chunkSize, voxelSize);
        
        // CRITICAL FIX: Expand search area to ensure we catch diagonal neighbors
        for (int x = minChunk.x - 1; x <= maxChunk.x + 1; x++)
        for (int y = minChunk.y - 1; y <= maxChunk.y + 1; y++)
        for (int z = minChunk.z - 1; z <= maxChunk.z + 1; z++)
        {
            Vector3Int chunkCoord = new Vector3Int(x, y, z);
            
            // Calculate chunk center for distance check
            Vector3 chunkCenter = Coord.GetWorldPosition(
                chunkCoord, 
                new Vector3Int(chunkSize/2, chunkSize/2, chunkSize/2), 
                chunkSize, 
                voxelSize
            );
            
            // CRITICAL FIX: Use a more generous margin for solid chunks
            float chunkMargin = chunkSize * voxelSize * 1.2f; // Increased from 0.8 to 1.2 (120% of chunk size)
            
            // CRITICAL FIX: Handle chunks without cache data more robustly
            // If cache doesn't exist, we can't know if it's solid, so use conservative approach
            bool hasCacheData = TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis);
            bool isSolidChunk = hasCacheData && analysis.IsSolid;
            bool isMarkedForModification = IsSolidChunkMarkedForModification(chunkCoord);
            
            // Use increased margin for solid chunks
            if (isSolidChunk)
            {
                chunkMargin *= 1.5f;
            }
            
            // Calculate distance from mining point to nearest chunk face instead of center
            float distanceToChunk = DistanceToChunkBounds(worldPos, chunkCoord);
            
            // CRITICAL FIX: If chunk is marked for modification, it was explicitly included
            // in a previous mining operation - always include it to ensure consistency
            if (isMarkedForModification)
            {
                affectedChunks.Add(chunkCoord);
                string prefix = IsProblematicCoordinate(chunkCoord) ? "[COORD_5_DEBUG]" : "";
                Debug.Log($"{prefix}[GetAffectedChunks] Added chunk {chunkCoord} - marked for modification, distance: {distanceToChunk:F2}");
                if (isSolidChunk)
                {
                    MarkSolidChunkForModification(chunkCoord);
                }
            }
            // If we're within range of the chunk, add it
            else if (distanceToChunk <= radius + voxelSize)
            {
                affectedChunks.Add(chunkCoord);
                string prefix = IsProblematicCoordinate(chunkCoord) ? "[COORD_5_DEBUG]" : "";
                Debug.Log($"{prefix}[GetAffectedChunks] Added chunk {chunkCoord} - distance: {distanceToChunk:F2}, radius: {radius + voxelSize:F2}");
                
                // If this chunk is solid, make sure we mark it for modification
                if (isSolidChunk)
                {
                    MarkSolidChunkForModification(chunkCoord);
                }
            }
            // DIAGNOSTIC: Log when problematic coordinates are NOT added
            else if (IsProblematicCoordinate(chunkCoord))
            {
                Debug.Log($"[COORD_5_DEBUG][GetAffectedChunks] SKIPPED chunk {chunkCoord} - distance: {distanceToChunk:F2}, radius: {radius + voxelSize:F2}, marked: {isMarkedForModification}, solid: {isSolidChunk}");
            }
            // Also include solid chunks that are just a bit further away
            else if (isSolidChunk && distanceToChunk <= radius * 1.5f)
            {
                Debug.Log($"[GetAffectedChunks] Including nearby solid chunk {chunkCoord} for potential modification - distance: {distanceToChunk:F2}");
                affectedChunks.Add(chunkCoord);
                MarkSolidChunkForModification(chunkCoord);
            }
            // CRITICAL FIX: For chunks without cache data that are close, use conservative approach
            // Include them if they're within extended radius to ensure boundary updates
            else if (!hasCacheData && distanceToChunk <= radius * 1.3f)
            {
                Debug.Log($"[GetAffectedChunks] Including chunk {chunkCoord} without cache data (conservative approach) - distance: {distanceToChunk:F2}");
                affectedChunks.Add(chunkCoord);
            }
            else
            {
                // DEBUG: Log chunks that are being excluded
                if (distanceToChunk <= radius * 2f) // Only log if reasonably close
                {
                    Debug.Log($"[GetAffectedChunks] Excluded chunk {chunkCoord} - distance: {distanceToChunk:F2}, radius: {radius + voxelSize:F2}, isSolid: {isSolidChunk}, hasCache: {hasCacheData}");
                }
            }
        }
        
        return affectedChunks;
    }

    // ADD new method to World.cs to calculate distance to chunk bounds
    private float DistanceToChunkBounds(Vector3 point, Vector3Int chunkCoord)
    {
        // Get chunk bounds
        Vector3 chunkMin = GetChunkWorldPosition(chunkCoord);
        Vector3 chunkMax = chunkMin + new Vector3(chunkSize, chunkSize, chunkSize) * voxelSize;
        
        // Calculate closest point on chunk bounds to the given point
        float closestX = Mathf.Max(chunkMin.x, Mathf.Min(point.x, chunkMax.x));
        float closestY = Mathf.Max(chunkMin.y, Mathf.Min(point.y, chunkMax.y));
        float closestZ = Mathf.Max(chunkMin.z, Mathf.Min(point.z, chunkMax.z));
        
        // Return distance to closest point
        return Vector3.Distance(point, new Vector3(closestX, closestY, closestZ));
    }

    private Vector3 ClampWorldPositionToChunk(Vector3 point, Vector3Int chunkCoord)
    {
        Vector3 chunkMin = GetChunkWorldPosition(chunkCoord);
        // CRITICAL FIX: Clamp to the last valid density point, not the start of the next chunk
        // Density grid has (chunkSize + 1) points, but the max valid position is at chunkSize * voxelSize - epsilon
        // We subtract a small epsilon to ensure we stay within the valid density grid range (0 to chunkSize)
        float epsilon = voxelSize * 0.001f; // Very small offset to stay within bounds
        Vector3 chunkMax = chunkMin + new Vector3(chunkSize, chunkSize, chunkSize) * voxelSize - Vector3.one * epsilon;
        
        float clampedX = Mathf.Clamp(point.x, chunkMin.x, chunkMax.x);
        float clampedY = Mathf.Clamp(point.y, chunkMin.y, chunkMax.y);
        float clampedZ = Mathf.Clamp(point.z, chunkMin.z, chunkMax.z);
        
        return new Vector3(clampedX, clampedY, clampedZ);
    }
    
    private Vector3 GetEffectiveBrushPosition(
        Vector3 sourcePosition,
        Vector3Int chunkCoord,
        float influenceRadius,
        bool allowClamp,
        out float originalDistance,
        out float effectiveDistance,
        out bool wasClamped)
    {
        originalDistance = DistanceToChunkBounds(sourcePosition, chunkCoord);
        wasClamped = allowClamp && originalDistance > influenceRadius;
        Vector3 adjustedPosition = sourcePosition;

        if (wasClamped)
        {
            adjustedPosition = ClampWorldPositionToChunk(sourcePosition, chunkCoord);
            effectiveDistance = DistanceToChunkBounds(adjustedPosition, chunkCoord);
        }
        else
        {
            effectiveDistance = originalDistance;
        }

        return adjustedPosition;
    }
    
    private void LogChunkModificationSummary(
        Vector3Int chunkCoord,
        Vector3 worldPos,
        Vector3 chunkOrigin,
        float brushRadius,
        Vector3Int localMiningPos,
        bool wasSolid,
        bool anyDensityChanged,
        int pointsProcessed,
        int pointsPassedFalloff,
        int pointsPassedThreshold,
        int pointsSetSuccessfully,
        int pointsSkippedFalloff,
        bool forceBoundaryFalloff)
    {
        if (!ShouldLogChunkDiagnostics(chunkCoord))
        {
            return;
        }

        Vector3 chunkMin = chunkOrigin;
        Vector3 chunkMax = chunkMin + new Vector3(chunkSize, chunkSize, chunkSize) * voxelSize;
        Vector3 clamped = ClampWorldPositionToChunk(worldPos, chunkCoord);
        float distanceToBounds = Vector3.Distance(worldPos, clamped);

        Debug.Log($"[MOD_DIAG:{chunkCoord}] brush={worldPos} clamped={clamped} distToBounds={distanceToBounds:F2} " +
                  $"radius={brushRadius:F2} local={localMiningPos} wasSolid={wasSolid} changed={anyDensityChanged} forceFalloff={forceBoundaryFalloff} " +
                  $"processed={pointsProcessed} passFalloff={pointsPassedFalloff} passThreshold={pointsPassedThreshold} " +
                  $"set={pointsSetSuccessfully} skippedFalloff={pointsSkippedFalloff}");
    }

    private float CalculateDensityAtPoint(Vector3 worldPos)
    {
        FastNoiseLite noise = new FastNoiseLite();
        noise.SetSeed(noiseSeed);
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFrequency(frequency);

        float noiseValue = noise.GetNoise(worldPos.x * noiseScale, worldPos.z * noiseScale);
        
        // Match the constants from DensityFieldGenerationJob
        const float NOISE_MULTIPLIER = 0.5f;
        const float DENSITY_BUFFER = -0.1f;
        const float POW_FACTOR = 1.5f;

        noiseValue = (noiseValue + 1f) * NOISE_MULTIPLIER;
        noiseValue = Mathf.Clamp(noiseValue, 0f, 1f);
        noiseValue = Mathf.Pow(noiseValue, POW_FACTOR);

        float terrainHeight = noiseValue * maxHeight;
        return worldPos.y - terrainHeight + DENSITY_BUFFER;
    }
    #endregion
    
    #region Player Movement Control
    private void UpdatePlayerMovementLock(bool shouldLock)
    {
        if (playerController == null)
            return;

        if (playerMovementLocked != shouldLock)
        {
            playerMovementLocked = shouldLock;

#if ENABLE_INPUT_SYSTEM
            var playerInputComponent = playerController.GetComponent<PlayerInput>();
            if (playerInputComponent != null)
            {
                playerInputComponent.enabled = !shouldLock;
            }
#endif
        }

        var controllerInputs = playerController.GetComponent<ControllerInputs>();
        if (controllerInputs != null && shouldLock)
        {
            controllerInputs.MoveInput(Vector2.zero);
            controllerInputs.LookInput(Vector2.zero);
            controllerInputs.JumpInput(false);
            controllerInputs.SprintInput(false);
            controllerInputs.PrimaryActionInput(false);
        }
    }
    #endregion

    public bool NotifyInitialEmptyChunkUnloaded(Vector3Int chunkCoord)
    {
        if (initialLoadStage == InitialLoadStage.Complete)
            return false;

        bool wasPending = initialLoadEmptyPendingUnload.Remove(chunkCoord);
        if (wasPending)
        {
            if (!initialLoadEmptyTracked.Contains(chunkCoord))
            {
                initialLoadEmptyTracked.Add(chunkCoord);
                initialLoadEmptyTotal = Mathf.Max(initialLoadEmptyTotal, initialLoadEmptyTracked.Count);
            }

            initialLoadEmptyProcessed = Mathf.Min(initialLoadEmptyProcessed + 1, initialLoadEmptyTracked.Count);
            UpdateInitialLoadProgressState();
        }

        return wasPending;
    }

    public void QueueVoxelUpdate(Vector3Int chunkCoord, Vector3Int voxelPos, bool isAdding, bool propagate)
    {
        lock (updateLock)
        {
            // If this is a mining operation (destruction with propagation), queue it for sequential processing
            if (!isAdding && propagate)
            {
                // Queue the mining operation instead of processing immediately
                if (!miningOperationQueues.ContainsKey(chunkCoord))
                {
                    miningOperationQueues[chunkCoord] = new Queue<Vector3Int>();
                }
                
                // Always queue - ProcessMiningQueues() will handle processing sequentially
                miningOperationQueues[chunkCoord].Enqueue(voxelPos);
                chunksWithQueuedMining.Add(chunkCoord);
                
                // Request chunk load if not already loaded
                if (!chunks.ContainsKey(chunkCoord))
                {
                    RequestChunkLoad(chunkCoord);
                }
                
                return;
            }

            if (!pendingVoxelUpdates.ContainsKey(chunkCoord))
            {
                pendingVoxelUpdates[chunkCoord] = new List<PendingVoxelUpdate>();
            }
            pendingVoxelUpdates[chunkCoord].Add(new PendingVoxelUpdate(voxelPos, isAdding, propagate));

            // Check if this chunk is marked as solid in the terrain analysis cache
            if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis.IsSolid)
            {
                // Mark this solid chunk for modification so it gets loaded
                MarkSolidChunkForModification(chunkCoord);
            }

            // Request chunk load if not already loaded
            if (!chunks.ContainsKey(chunkCoord))
            {
                RequestChunkLoad(chunkCoord);
            }
        }
    }

    public void QueueDensityUpdate(Vector3Int chunkCoord, Vector3 worldPos)
    {
        lock (updateLock)
        {
            // Get chunk origin and bounds
            Vector3 chunkOrigin = GetChunkWorldPosition(chunkCoord);
            float chunkWorldSize = chunkSize * voxelSize;
            Vector3 chunkMin = chunkOrigin;
            Vector3 chunkMax = chunkOrigin + Vector3.one * chunkWorldSize;
            
            // CRITICAL FIX: Use the EXACT same distance calculation as GetAffectedChunks
            // This ensures chunks identified as "affected" are not filtered out here
            // Calculate radius using the SAME formula as HandleVoxelDestruction
            float radius = voxelSize * (Config.densityInfluenceRadius + 1f); // Same as HandleVoxelDestruction
            
            // Use DistanceToChunkBounds (same as GetAffectedChunks) instead of axis-aligned bounds
            // This ensures consistency - if GetAffectedChunks says a chunk is affected, 
            // QueueDensityUpdate won't filter it out
            float distanceToChunk = DistanceToChunkBounds(worldPos, chunkCoord);
            
            // CRITICAL FIX: Check if this chunk is marked for modification or has pending updates
            // If so, it was explicitly included in affectedChunks and should NOT be filtered out
            // This handles the race condition where cache might be invalidated between GetAffectedChunks and QueueDensityUpdate
            bool isMarkedForModification = IsSolidChunkMarkedForModification(chunkCoord);
            bool hasPendingUpdates = HasPendingUpdates(chunkCoord);
            bool isForcedForLoad = IsChunkForcedForLoad(chunkCoord);
            
            // Check if this is a solid chunk (same logic as GetAffectedChunks)
            // CRITICAL: If cache was invalidated, analysis might be null, so check that first
            bool isSolidChunk = false;
            bool hasCacheData = TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis);
            if (hasCacheData)
            {
                isSolidChunk = analysis.IsSolid;
            }
            
            Vector3 originalWorldPos = worldPos;
            
            // CRITICAL: Do NOT clamp the mining center position here!
            // All chunks must use the same mining center for consistent spherical falloff across boundaries.
            // The actual density point filtering happens in ApplyDensityUpdate based on distance.
            Vector3 effectiveWorldPos = worldPos;
            
            float originalDistance = distanceToChunk;
            bool boundaryClampAllowed = distanceToChunk > radius && (isMarkedForModification || hasPendingUpdates || isForcedForLoad);
            float effectiveDistance = distanceToChunk;
            bool wasClamped = false;
            Vector3 clampedWorldPos = effectiveWorldPos; // Start from the chunk-bounds-clamped position
            
            if (boundaryClampAllowed)
            {
                // Calculate effective distance for filtering, but DON'T change effectiveWorldPos
                // We need consistent mining center across all chunks for proper spherical falloff
                clampedWorldPos = GetEffectiveBrushPosition(
                    originalWorldPos,
                    chunkCoord,
                    radius,
                    boundaryClampAllowed,
                    out originalDistance,
                    out effectiveDistance,
                    out wasClamped);
                
                // NOTE: We intentionally do NOT update effectiveWorldPos here!
                // effectiveWorldPos must remain the original mining center for all chunks
                
                if (wasClamped && ShouldLogChunkDiagnostics(chunkCoord))
                {
                    Debug.Log($"[MOD_DIAG:{chunkCoord}] Boundary clamp -> source={originalWorldPos} adjusted={clampedWorldPos} sourceDist={originalDistance:F2} radius={radius:F2}");
                }
            }
            else
            {
                effectiveDistance = distanceToChunk;
            }
            
            distanceToChunk = effectiveDistance;
            
            if (ShouldLogChunkDiagnostics(chunkCoord) || IsProblematicCoordinate(chunkCoord))
            {
                string prefix = IsProblematicCoordinate(chunkCoord) ? "[COORD_5_DEBUG]" : $"[MOD_DIAG:{chunkCoord}]";
                Debug.Log($"{prefix} QueueDensityUpdate -> source={originalWorldPos} brush={effectiveWorldPos} " +
                          $"sourceDist={originalDistance:F2} distToBounds={distanceToChunk:F2} radius={radius:F2} " +
                          $"pending={hasPendingUpdates} forced={isForcedForLoad} marked={isMarkedForModification} solid={isSolidChunk} cacheHit={hasCacheData}");
            }
            
            // CRITICAL FIX: If chunk is marked for modification or already has pending updates,
            // it was explicitly included in affectedChunks - don't filter it out!
            // Use extended radius check to match GetAffectedChunks behavior for solid chunks
            bool skipDistanceFiltering = isMarkedForModification || hasPendingUpdates || isForcedForLoad;
            if (skipDistanceFiltering)
            {
                // This chunk was explicitly included in affectedChunks - use extended radius
                // to match GetAffectedChunks behavior (radius * 1.5f for solid chunks)
                float effectiveRadius = isSolidChunk ? radius * 1.5f : radius + voxelSize;
                if (distanceToChunk > effectiveRadius * 1.2f) // Add 20% margin for safety
                {
                    Debug.LogWarning($"[QueueDensityUpdate] Chunk {chunkCoord} exceeded extended radius but is protected (distance: {distanceToChunk:F2}, " +
                        $"effectiveRadius: {effectiveRadius * 1.2f:F2}, Marked:{isMarkedForModification}, Pending:{hasPendingUpdates}, Forced:{isForcedForLoad})");
                    // Still allow it - it was explicitly marked, so trust that it needs the update
                }
                Debug.Log($"[QueueDensityUpdate] Chunk {chunkCoord} exempt from distance filter (Marked:{isMarkedForModification}, Pending:{hasPendingUpdates}, Forced:{isForcedForLoad})");
            }
            
            // Use the EXACT same distance checks as GetAffectedChunks:
            // - Normal chunks: distanceToChunk <= radius + voxelSize
            // - Solid chunks: distanceToChunk <= radius * 1.5f
            bool withinRange = false;
            if (isSolidChunk)
            {
                withinRange = distanceToChunk <= radius * 1.5f; // Same as GetAffectedChunks for solid chunks
            }
            else
            {
                withinRange = distanceToChunk <= radius + voxelSize; // Same as GetAffectedChunks for normal chunks
            }
            
            // CRITICAL FIX: Don't filter out chunks that are marked for modification or have pending updates
            // These were explicitly included in affectedChunks and should always get updates
            // Note: effectiveWorldPos is already clamped to chunk bounds at the start of this function
            bool forceBoundaryFalloff = false;
            
            if (!withinRange && !skipDistanceFiltering)
            {
                // DEBUG: Log when updates are filtered out by distance
                Debug.LogWarning($"[QueueDensityUpdate] Chunk {chunkCoord} filtered out - worldPos {originalWorldPos} too far. " +
                    $"Distance to chunk bounds: {distanceToChunk:F2}, " +
                    $"Radius check: {(isSolidChunk ? radius * 1.5f : radius + voxelSize):F2}, " +
                    $"IsSolid: {isSolidChunk}, Chunk bounds: {chunkMin} to {chunkMax}");
                LogChunkTrace(chunkCoord, $"Density update FILTERED OUT - distance {distanceToChunk:F2} > radius {(isSolidChunk ? radius * 1.5f : radius + voxelSize):F2}");
                return;
            }
            else if (!withinRange && skipDistanceFiltering)
            {
                // When forced through despite being out of range, use boundary falloff for solid chunks
                forceBoundaryFalloff = isSolidChunk;
                float clampDelta = Vector3.Distance(originalWorldPos, effectiveWorldPos);
                
                Debug.Log($"[QueueDensityUpdate] Chunk {chunkCoord} outside normal radius but forced through (Marked:{isMarkedForModification}, Pending:{hasPendingUpdates}, Forced:{isForcedForLoad}). " +
                    $"OriginalPos: {originalWorldPos}, ClampedPos: {effectiveWorldPos}, ClampDelta: {clampDelta:F2}, ForceFalloff: {forceBoundaryFalloff}");
                LogChunkTrace(chunkCoord, $"QueueDensityUpdate: FORCED path - originalPos {originalWorldPos}, clampedPos {effectiveWorldPos}, clampDelta {clampDelta:F2}, ForceFalloff: {forceBoundaryFalloff}");
            }
            
            // DEBUG: Log successful queue attempts
            Debug.Log($"[QueueDensityUpdate] Queuing density update for chunk {chunkCoord} at worldPos {effectiveWorldPos} (original: {originalWorldPos}, forceFalloff={forceBoundaryFalloff})");
            LogChunkTrace(chunkCoord, $"Density update queued successfully - effectivePos: {effectiveWorldPos}, originalPos: {originalWorldPos}, distance: {distanceToChunk:F2}, forceFalloff: {forceBoundaryFalloff}");

            if (!pendingDensityPointUpdates.ContainsKey(chunkCoord))
            {
                pendingDensityPointUpdates[chunkCoord] = new List<PendingDensityPointUpdate>();
            }

            // CRITICAL FIX: Use the effectiveWorldPos (clamped if necessary) instead of originalWorldPos
            // This ensures that when mining outside a chunk, we update the boundary points correctly
            Vector3Int localPos = Coord.WorldToVoxelCoord(effectiveWorldPos, chunkOrigin, voxelSize);
            
            // For boundary updates, we need to allow voxel coordinates from -1 to chunkSize-1
            // This ensures density points at 0 (voxel -1) and chunkSize (voxel chunkSize-1) can be updated
            // Valid density range is 0 to chunkSize (inclusive), so voxel range is -1 to chunkSize-1
            localPos = new Vector3Int(
                Mathf.Clamp(localPos.x, -1, chunkSize - 1), // Voxel -1 → Density 0, Voxel chunkSize-1 → Density chunkSize
                Mathf.Clamp(localPos.y, -1, chunkSize - 1),
                Mathf.Clamp(localPos.z, -1, chunkSize - 1)
            );
            
            // Now convert to density position (adds 1 to each coordinate)
            Vector3Int densityPos = Coord.VoxelToDensityCoord(localPos);
            
            // Verify the position is valid (density points are 0 to chunkSize inclusive)
            if (!Coord.IsDensityPositionValid(densityPos, chunkSize + 1))
            {
                // This shouldn't happen with proper clamping, but log and skip if it does
                return;
            }

            // Calculate new density value (higher value = outside surface)
            float targetDensity = surfaceLevel + 1.5f; // Ensure we create a hole
            
            // Handle solid chunks specially (isSolidChunk already determined above)
            if (isSolidChunk)
            {
                // For solid chunks, use a higher value to ensure proper modification
                targetDensity = surfaceLevel + 2.0f;
                Debug.Log($"Queuing special solid chunk update for {chunkCoord} at {densityPos} with density {targetDensity}");
            }
            
            // CRITICAL FIX: Use effectiveWorldPos (which may be clamped) instead of originalWorldPos
            // This ensures ApplyDensityUpdate receives the correct position for boundary chunks
            pendingDensityPointUpdates[chunkCoord].Add(
                new PendingDensityPointUpdate(densityPos, targetDensity, effectiveWorldPos, forceBoundaryFalloff));
            
            // CRITICAL FIX: If this is a solid chunk, mark it for modification immediately
            if (isSolidChunk)
            {
                // Mark for modification and update terrain analysis to reflect this
                MarkSolidChunkForModification(chunkCoord);
            }
            
            // CRITICAL: Always request load for unloaded chunks, even if they're in an invalid state
            // This ensures chunks with pending updates will eventually load
            if (!chunks.ContainsKey(chunkCoord))
            {
                var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                bool isQuarantined = ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord);
                bool chunkHasPendingUpdates = pendingDensityPointUpdates.ContainsKey(chunkCoord);
                bool chunkHasPendingVoxels = pendingVoxelUpdates.ContainsKey(chunkCoord);
                bool shouldForceLoad = chunkHasPendingUpdates || chunkHasPendingVoxels || isForcedForLoad;
                
                // DEBUG: Log chunk state when requesting load
                Debug.Log($"[QueueDensityUpdate] Requesting load for chunk {chunkCoord} - " +
                    $"State: {state.Status}, Quarantined: {isQuarantined}, Solid: {isSolidChunk}, " +
                    $"HasPendingUpdates: {chunkHasPendingUpdates}, HasPendingVoxels: {chunkHasPendingVoxels}, ForcedFlag: {isForcedForLoad}, " +
                    $"PendingCount: {(chunkHasPendingUpdates ? pendingDensityPointUpdates[chunkCoord].Count : 0)}");
                LogChunkTrace(chunkCoord, $"Load requested - State: {state.Status}, Quarantined: {isQuarantined}, Solid: {isSolidChunk}, HasPendingUpdates: {chunkHasPendingUpdates}, Forced: {isForcedForLoad}");
                
                if (isQuarantined)
                {
                    Debug.LogWarning($"[QueueDensityUpdate] Chunk {chunkCoord} is QUARANTINED - cannot load! But has pending updates!");
                    LogChunkTrace(chunkCoord, "WARNING: Chunk is QUARANTINED but has pending updates!");
                    // Don't return - still queue the update so it can be applied if chunk loads later
                }
                else if (state.Status != ChunkConfigurations.ChunkStatus.Loading)
                {
                    // CRITICAL FIX: Always request load if chunk has pending updates, regardless of state
                    // This ensures chunks with pending/forced updates will eventually load
                    if (shouldForceLoad)
                    {
                        Debug.Log($"[QueueDensityUpdate] Chunk {chunkCoord} has pending/forced updates - forcing load request");
                    }
                    
                    if (isSolidChunk)
                    {
                        // Use the direct loading method for solid chunks
                        Debug.Log($"[QueueDensityUpdate] Loading solid chunk {chunkCoord} immediately");
                        LoadChunkImmediately(chunkCoord);
                    }
                    else
                    {
                        Debug.Log($"[QueueDensityUpdate] Requesting immediate load for chunk {chunkCoord}");
                        RequestImmediateChunkLoad(chunkCoord);
                    }
                }
                else
                {
                    Debug.Log($"[QueueDensityUpdate] Chunk {chunkCoord} already loading (state: {state.Status}) - update will be applied when loaded");
                }
            }
            else
            {
                Debug.Log($"[QueueDensityUpdate] Chunk {chunkCoord} already loaded - update will be applied via ProcessPendingUpdates");
            }
        }
    }

    public void RequestChunkLoad(Vector3Int chunkCoord, bool force = false)
    {
        bool isQuarantined = ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord);
        bool hasPendingUpdates = HasPendingUpdates(chunkCoord);
        bool shouldForceLoad = force || hasPendingUpdates;
        
        if (isQuarantined && !shouldForceLoad)
        {
            Debug.LogWarning($"[RequestChunkLoad] Skipping load request for QUARANTINED chunk {chunkCoord} (no pending updates)");
            LogChunkTrace(chunkCoord, $"RequestChunkLoad: Skipped - quarantined with no pending updates");
            return;
        }
        
        if (isQuarantined && shouldForceLoad)
        {
            Debug.LogWarning($"[RequestChunkLoad] Allowing load request for QUARANTINED chunk {chunkCoord} (force={force}, pending updates={hasPendingUpdates})");
            LogChunkTrace(chunkCoord, $"RequestChunkLoad: Allowing quarantined chunk load - force={force}, pending={hasPendingUpdates}");
            ChunkStateManager.Instance.QuarantinedChunks.Remove(chunkCoord);
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            if (state.Status == ChunkConfigurations.ChunkStatus.Error)
            {
                ChunkStateManager.Instance.TryChangeState(
                    chunkCoord,
                    ChunkConfigurations.ChunkStatus.None,
                    ChunkConfigurations.ChunkStateFlags.None);
            }
        }

        // Skip if chunk is already loaded or in queue
        if (!chunks.ContainsKey(chunkCoord))
        {
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            
            // DEBUG: Log load request details
            Debug.Log($"[RequestChunkLoad] Chunk {chunkCoord} - " +
                $"State: {state.Status}, Quarantined: {isQuarantined}, HasPendingUpdates: {hasPendingUpdates}, Force: {force}, " +
                $"VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}, " +
                $"DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}");
            LogChunkTrace(chunkCoord, $"RequestChunkLoad called - State: {state.Status}, Quarantined: {isQuarantined}, HasPendingUpdates: {hasPendingUpdates}, Force: {force}, Immediate: {shouldForceLoad}, QuickCheck: {!shouldForceLoad}");
                                    
            try
            {
                operationsQueue.QueueChunkForLoad(
                    chunkCoord,
                    immediate: shouldForceLoad,
                    quickCheck: !shouldForceLoad);
                
                Debug.Log($"[RequestChunkLoad] Successfully queued chunk {chunkCoord} for load (immediate: {shouldForceLoad}, quickCheck: {!shouldForceLoad})");
                LogChunkTrace(chunkCoord, $"Successfully queued for load - Immediate: {shouldForceLoad}, QuickCheck: {!shouldForceLoad}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[RequestChunkLoad] Failed to queue chunk {chunkCoord}: {e.Message}");
                OnChunkLoadFailed(chunkCoord, e.Message);
            }
        }
        else
        {
            Debug.Log($"[RequestChunkLoad] Chunk {chunkCoord} already loaded");
        }
    }
    
    /// <summary>
    /// Process pending updates for a specific chunk immediately (called when chunk finishes loading)
    /// </summary>
    private void ProcessPendingUpdatesForChunk(Vector3Int chunkCoord)
    {
        lock (updateLock)
        {
            if (!chunks.TryGetValue(chunkCoord, out Chunk chunk))
            {
                Debug.LogWarning($"[ProcessPendingUpdatesForChunk] Chunk {chunkCoord} not found in chunks dictionary");
                return;
            }
            
            // Process density updates for this chunk
            if (pendingDensityPointUpdates.ContainsKey(chunkCoord))
            {
                bool isSolidChunk = false;
                bool isEmptyChunk = false;
                if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis != null)
                {
                    isSolidChunk = analysis.IsSolid;
                    isEmptyChunk = analysis.IsEmpty;
                }
                
                // CRITICAL FIX: Initialize density arrays for QuickCheck chunks BEFORE processing updates
                // QuickCheck chunks skip full terrain generation, so their density arrays may be uninitialized
                // We need to set baseline values (high for solid, low for empty) before applying modifications
                // Check both cache and chunk's own data to determine if it's a QuickCheck chunk
                var chunkDataForInit = chunk.GetChunkData();
                bool chunkIsSolid = chunkDataForInit != null && chunkDataForInit.IsSolidChunk;
                bool chunkIsEmpty = chunkDataForInit != null && chunkDataForInit.IsEmptyChunk;
                
                if (isSolidChunk || isEmptyChunk || chunkIsSolid || chunkIsEmpty)
                {
                    Debug.Log($"[ProcessPendingUpdatesForChunk] Initializing QuickCheck chunk {chunkCoord} (Cache: Solid={isSolidChunk}, Empty={isEmptyChunk}, Chunk: Solid={chunkIsSolid}, Empty={chunkIsEmpty}) before processing updates");
                    LogChunkTrace(chunkCoord, $"ProcessPendingUpdatesForChunk: Initializing QuickCheck chunk before processing updates");
                    chunk.InitializeQuickCheckChunkDensity();
                }
                
                var updates = new List<PendingDensityPointUpdate>(pendingDensityPointUpdates[chunkCoord]);
                pendingDensityPointUpdates.Remove(chunkCoord);  // Remove immediately after copying
                
                Debug.Log($"[ProcessPendingUpdatesForChunk] Processing {updates.Count} density updates for chunk {chunkCoord}");
                LogChunkTrace(chunkCoord, $"ProcessPendingUpdatesForChunk: Processing {updates.Count} density updates");
                
                // CRITICAL FIX: Process ALL queued updates, not just the first one
                // Multiple mining operations can queue multiple updates for the same chunk
                bool anyDensityChanged = false;
                foreach (var update in updates)
                {
                    Vector3 worldPos = update.worldPosition;
                    
                    // Use ApplyDensityUpdate for proper radius-based update
                    bool densityChanged = ApplyDensityUpdate(chunk, worldPos, false, update.forceBoundaryFalloff);
                    if (densityChanged)
                    {
                        anyDensityChanged = true;
                        Debug.Log($"[ProcessPendingUpdatesForChunk] Density changed for chunk {chunkCoord} at worldPos {worldPos}");
                        LogChunkTrace(chunkCoord, $"ProcessPendingUpdatesForChunk: Density changed successfully at {worldPos}");
                    }
                    else
                    {
                        Debug.LogWarning($"[ProcessPendingUpdatesForChunk] Density update for chunk {chunkCoord} at {worldPos} did not change density values - may already be modified");
                    }
                }
                
                if (anyDensityChanged)
                {
                    Debug.Log($"[ProcessPendingUpdatesForChunk] Processed {updates.Count} updates for chunk {chunkCoord}, density changed - marking as modified");
                    LogChunkTrace(chunkCoord, $"ProcessPendingUpdatesForChunk: Density changed successfully - marking as modified");
                    ClearForcedChunkFlag(chunkCoord, "Density applied via ProcessPendingUpdatesForChunk");
                    
                    // Update terrain analysis immediately
                    TerrainAnalysisCache.InvalidateAnalysis(chunkCoord);
                    
                    // CRITICAL FIX: If this was a solid chunk, properly mark as modified
                    if (isSolidChunk)
                    {
                        TerrainAnalysisCache.SaveAnalysis(chunkCoord, false, false, true);
                    }
                    
                    ChunkStateManager.Instance.TryChangeState(
                        chunkCoord,
                        ChunkConfigurations.ChunkStatus.Modified,
                        ChunkConfigurations.ChunkStateFlags.Active
                    );
                    
                    // Queue mesh update
                    chunk.isMeshUpdateQueued = true;
                    chunksNeedingMeshUpdate.Add(chunk);
                    
                    // Force chunk to save
                    var chunkData = chunk.GetChunkData();
                    if (chunkData != null && chunkData.HasModifiedData)
                    {
                        chunkData.SaveData();
                    }
                }
                else
                {
                    Debug.LogWarning($"[ProcessPendingUpdatesForChunk] NONE of the {updates.Count} density updates for chunk {chunkCoord} changed density values");
                }
            }
            
            // Process voxel updates for this chunk
            if (pendingVoxelUpdates.ContainsKey(chunkCoord))
            {
                var updates = new List<PendingVoxelUpdate>(pendingVoxelUpdates[chunkCoord]);
                pendingVoxelUpdates.Remove(chunkCoord);  // Remove immediately after copying
                
                Debug.Log($"[ProcessPendingUpdatesForChunk] Processing {updates.Count} voxel updates for chunk {chunkCoord}");
                
                foreach (var update in updates)
                {
                    if (update.isAdding)
                        chunk.AddVoxel(update.voxelPosition);
                    else
                        chunk.DamageVoxel(update.voxelPosition, 1);
                }
                
                // Queue mesh update
                chunk.isMeshUpdateQueued = true;
                chunksNeedingMeshUpdate.Add(chunk);
                
                // Force chunk to save
                var chunkData = chunk.GetChunkData();
                if (chunkData != null && chunkData.HasModifiedData)
                {
                    chunkData.SaveData();
                }
            }
        }
    }
    
    private void ProcessPendingUpdates()
    {
        try
        {
            if (Time.time - lastUpdateTime < Config.updateInterval) return;
            lastUpdateTime = Time.time;
            
            // CRITICAL: Check if required instances are available
            if (MeshDataPool.Instance == null || ChunkStateManager.Instance == null)
            {
                // Instances not ready yet, skip this frame
                return;
            }
            
            // CRITICAL: Store instance references locally to prevent null reference if instance is destroyed during execution
            MeshDataPool meshDataPool = MeshDataPool.Instance;
            ChunkStateManager chunkStateManager = ChunkStateManager.Instance;
            
            // CRITICAL: Re-check instances after storing references
            if (meshDataPool == null || chunkStateManager == null)
            {
                return;
            }
            
            int updatesProcessed = 0;
            int maxUpdatesThisFrame;
            
            try
            {
                maxUpdatesThisFrame = meshDataPool.GetDynamicChunksPerFrame();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ProcessPendingUpdates] Error getting max updates per frame: {e.Message}");
                return;
            }
            
            // To track chunks that had density modifications
            HashSet<Vector3Int> modifiedChunks = new HashSet<Vector3Int>();
                
            lock (updateLock)
            {
                // CRITICAL: Check if dictionaries are initialized
                if (pendingVoxelUpdates == null || pendingDensityPointUpdates == null)
                {
                    Debug.LogError("[ProcessPendingUpdates] Pending update dictionaries are null!");
                    return;
                }
                
                // Get a snapshot of the keys we need to process
                Vector3Int[] voxelKeys;
                Vector3Int[] densityKeys;
                
                try
                {
                    voxelKeys = pendingVoxelUpdates.Keys.ToArray();
                    densityKeys = pendingDensityPointUpdates.Keys.ToArray();
                    
                    // DEBUG: Log pending updates summary
                    if (voxelKeys.Length > 0 || densityKeys.Length > 0)
                    {
                        Debug.Log($"[ProcessPendingUpdates] Processing pending updates - " +
                            $"VoxelUpdates: {voxelKeys.Length} chunks, DensityUpdates: {densityKeys.Length} chunks, " +
                            $"MaxPerFrame: {maxUpdatesThisFrame}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ProcessPendingUpdates] Error creating key snapshots: {e.Message}\n{e.StackTrace}");
                    return;
                }

                // CRITICAL: Check if voxelKeys array is valid
                if (voxelKeys == null)
                {
                    Debug.LogError("[ProcessPendingUpdates] voxelKeys array is null!");
                    return;
                }
                
                // Process voxel updates using the snapshot
                foreach (var chunkCoord in voxelKeys)
                {
                    if (updatesProcessed >= maxUpdatesThisFrame) break;
                    
                    if (pendingVoxelUpdates.ContainsKey(chunkCoord))  // Check if still exists
                    {
                        if (chunks.TryGetValue(chunkCoord, out Chunk chunk) && chunk != null)
                        {
                            try
                            {
                                var updateList = pendingVoxelUpdates[chunkCoord];
                                if (updateList == null)
                                {
                                    Debug.LogWarning($"[ProcessPendingUpdates] Update list is null for chunk {chunkCoord} - removing entry");
                                    pendingVoxelUpdates.Remove(chunkCoord);
                                    continue;
                                }
                                
                                var updates = new List<PendingVoxelUpdate>(updateList);
                                pendingVoxelUpdates.Remove(chunkCoord);  // Remove immediately after copying

                                foreach (var update in updates)
                                {
                                    if (chunk == null) break; // Chunk was destroyed during processing
                                    
                                    if (update.isAdding)
                                        chunk.AddVoxel(update.voxelPosition);
                                    else
                                        chunk.DamageVoxel(update.voxelPosition, 1);
                                }
                                modifiedChunks.Add(chunkCoord);
                                updatesProcessed++;
                            }
                            catch (System.Exception e)
                            {
                                Debug.LogError($"[ProcessPendingUpdates] Error processing voxel updates for chunk {chunkCoord}: {e.Message}");
                            }
                        }
                    }
                    else
                    {
                        // CRITICAL FIX: Check if this is a solid chunk
                        if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis.IsSolid)
                        {
                            // Ensure solid chunks are properly marked
                            MarkSolidChunkForModification(chunkCoord);
                            // Try to force load the chunk
                            LoadChunkImmediately(chunkCoord);
                            // Keep updates for next frame
                        }
                        else
                        {
                            pendingVoxelUpdates.Remove(chunkCoord);  // Remove if chunk doesn't exist and isn't solid
                        }
                    }
                }

                // Only process density updates if we haven't hit our limit
                if (updatesProcessed < maxUpdatesThisFrame)
                {
                    // CRITICAL: Check if chunks dictionary is initialized
                    if (chunks == null)
                    {
                        Debug.LogError("[ProcessPendingUpdates] chunks dictionary is null!");
                        return;
                    }
                    
                    // CRITICAL: Check if densityKeys array is valid
                    if (densityKeys == null)
                    {
                        Debug.LogError("[ProcessPendingUpdates] densityKeys array is null!");
                        return;
                    }
                    
                    // Process density updates using the snapshot
                    foreach (var chunkCoord in densityKeys)
                    {
                        try
                        {
                            if (updatesProcessed >= maxUpdatesThisFrame) break;
                            
                            if (pendingDensityPointUpdates == null || !pendingDensityPointUpdates.ContainsKey(chunkCoord))  // Check if still exists
                            {
                                continue;
                            }
                            
                            // CRITICAL: Wrap TerrainAnalysisCache access in try-catch
                            bool isSolidChunk = false;
                            bool isEmptyChunk = false;
                            try
                            {
                                if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis != null)
                                {
                                    isSolidChunk = analysis.IsSolid;
                                    isEmptyChunk = analysis.IsEmpty;
                                }
                            }
                            catch (System.Exception e)
                            {
                                Debug.LogError($"[ProcessPendingUpdates] Error getting terrain analysis for {chunkCoord}: {e.Message}");
                                // Continue with default values
                            }
                            
                            if (chunks == null)
                            {
                                Debug.LogError("[ProcessPendingUpdates] chunks dictionary became null during processing!");
                                break;
                            }
                            
                            if (chunks.TryGetValue(chunkCoord, out Chunk chunk))
                            {
                                // CRITICAL: Double-check instances are still available
                                if (chunkStateManager == null || chunk == null)
                                {
                                    Debug.LogWarning($"[ProcessPendingUpdates] ChunkStateManager or chunk became null for {chunkCoord} - skipping");
                                    continue;
                                }
                                
                                // CRITICAL FIX: Only process updates for chunks that are fully loaded (Loaded or Modified state)
                                // Chunks in Loading state are not ready for density updates yet
                                ChunkStateManager.ChunkState state;
                                try
                                {
                                    state = chunkStateManager.GetChunkState(chunkCoord);
                                }
                                catch (System.Exception e)
                                {
                                    Debug.LogError($"[ProcessPendingUpdates] Error getting chunk state for {chunkCoord}: {e.Message}");
                                    continue;
                                }
                                if (state.Status != ChunkConfigurations.ChunkStatus.Loaded && 
                                    state.Status != ChunkConfigurations.ChunkStatus.Modified)
                                {
                                    // Chunk is still loading - keep updates for later
                                    LogChunkTrace(chunkCoord, $"ProcessPendingUpdates: Skipping chunk in {state.Status} state - will process when loaded");
                                    Debug.Log($"[ProcessPendingUpdates] Chunk {chunkCoord} is in {state.Status} state - skipping updates until fully loaded");
                                    continue; // Skip this chunk, keep updates for when it finishes loading
                                }
                                
                                // CRITICAL FIX: Initialize density arrays for QuickCheck chunks BEFORE processing updates
                                // QuickCheck chunks skip full terrain generation, so their density arrays may be uninitialized
                                // We need to set baseline values (high for solid, low for empty) before applying modifications
                                // Check both cache and chunk's own data to determine if it's a QuickCheck chunk
                                // CRITICAL: Check if chunk is still valid before accessing its data
                                if (chunk == null)
                                {
                                    Debug.LogWarning($"[ProcessPendingUpdates] Chunk {chunkCoord} became null - skipping");
                                    continue;
                                }
                                
                                var chunkData = chunk.GetChunkData();
                                bool chunkIsSolid = chunkData != null && chunkData.IsSolidChunk;
                                bool chunkIsEmpty = chunkData != null && chunkData.IsEmptyChunk;
                                
                                // CRITICAL: Always check if chunk needs initialization, even if not detected as QuickCheck
                                // Some chunks might have uninitialized density arrays even if not marked as empty/solid
                                bool needsInitialization = isSolidChunk || isEmptyChunk || chunkIsSolid || chunkIsEmpty;
                                
                                if (needsInitialization)
                                {
                                    Debug.Log($"[ProcessPendingUpdates] Initializing QuickCheck chunk {chunkCoord} (Cache: Solid={isSolidChunk}, Empty={isEmptyChunk}, Chunk: Solid={chunkIsSolid}, Empty={chunkIsEmpty}) before processing updates");
                                    LogChunkTrace(chunkCoord, $"ProcessPendingUpdates: Initializing QuickCheck chunk before processing updates");
                                    
                                    // Double-check chunk is still valid before calling initialization
                                    if (chunk != null)
                                    {
                                        try
                                        {
                                            chunk.InitializeQuickCheckChunkDensity();
                                        }
                                        catch (System.Exception e)
                                        {
                                            Debug.LogError($"[ProcessPendingUpdates] Error initializing QuickCheck chunk {chunkCoord}: {e.Message}");
                                            LogChunkTrace(chunkCoord, $"ProcessPendingUpdates: ERROR initializing QuickCheck chunk: {e.Message}");
                                        }
                                    }
                                }
                                else
                                {
                                    // Log when we're NOT initializing - this helps diagnose why updates might fail
                                    Debug.Log($"[ProcessPendingUpdates] Chunk {chunkCoord} is NOT a QuickCheck chunk (Cache: Solid={isSolidChunk}, Empty={isEmptyChunk}, Chunk: Solid={chunkIsSolid}, Empty={chunkIsEmpty}) - skipping initialization");
                                    LogChunkTrace(chunkCoord, $"ProcessPendingUpdates: Not a QuickCheck chunk - skipping initialization");
                                }
                                
                                // CRITICAL FIX: For loaded chunks, use ApplyDensityUpdate instead of single-point updates
                                // This ensures proper radius-based updates that prevent seams at boundaries
                                // Get the world position from the first queued update (they should all be from the same mining operation)
                                List<PendingDensityPointUpdate> updateList;
                                try
                                {
                                    updateList = pendingDensityPointUpdates[chunkCoord];
                                    if (updateList == null)
                                    {
                                        Debug.LogWarning($"[ProcessPendingUpdates] Density update list is null for chunk {chunkCoord} - removing entry");
                                        pendingDensityPointUpdates.Remove(chunkCoord);
                                        continue;
                                    }
                                }
                                catch (System.Exception e)
                                {
                                    Debug.LogError($"[ProcessPendingUpdates] Error accessing density updates for chunk {chunkCoord}: {e.Message}");
                                    continue;
                                }
                                
                                var updates = new List<PendingDensityPointUpdate>(updateList);
                                pendingDensityPointUpdates.Remove(chunkCoord);  // Remove immediately after copying
                                
                                Debug.Log($"[ProcessPendingUpdates] Processing {updates.Count} density updates for loaded chunk {chunkCoord} (state: {state.Status})");
                                LogChunkTrace(chunkCoord, $"ProcessPendingUpdates: Processing {updates.Count} density updates - State: {state.Status}");
                                
                                // CRITICAL FIX: Process ALL queued updates, not just the first one
                                // Multiple mining operations can queue multiple updates for the same chunk
                                // We need to apply ALL of them to ensure no modifications are lost
                                bool anyDensityChanged = false;
                                foreach (var update in updates)
                                {
                                    // CRITICAL: Check if chunk is still valid before processing each update
                                    if (chunk == null || !chunks.ContainsKey(chunkCoord))
                                    {
                                        Debug.LogWarning($"[ProcessPendingUpdates] Chunk {chunkCoord} became null or was removed during processing - stopping updates");
                                        LogChunkTrace(chunkCoord, $"ProcessPendingUpdates: Chunk became null during processing");
                                        break; // Stop processing remaining updates for this chunk
                                    }
                                    
                                    Vector3 worldPos = update.worldPosition;
                                    
                                    try
                                    {
                                        // Use ApplyDensityUpdate for proper radius-based update
                                bool densityChanged = ApplyDensityUpdate(chunk, worldPos, false, update.forceBoundaryFalloff);
                                        if (densityChanged)
                                        {
                                            anyDensityChanged = true;
                                            Debug.Log($"[ProcessPendingUpdates] Density changed for chunk {chunkCoord} at worldPos {worldPos}");
                                            LogChunkTrace(chunkCoord, $"ProcessPendingUpdates: Density changed successfully at {worldPos}");
                        ClearForcedChunkFlag(chunkCoord, "Density applied via ProcessPendingUpdates");
                                    ClearForcedChunkFlag(chunkCoord, "Density applied via ProcessPendingUpdates");
                                        }
                                        else
                                        {
                                            Debug.LogWarning($"[ProcessPendingUpdates] Density update for chunk {chunkCoord} at {worldPos} did not change density values - may already be modified");
                                            LogChunkTrace(chunkCoord, $"ProcessPendingUpdates: Density update at {worldPos} did not change values");
                                        }
                                    }
                                    catch (System.Exception e)
                                    {
                                        Debug.LogError($"[ProcessPendingUpdates] Error applying density update to chunk {chunkCoord} at {worldPos}: {e.Message}");
                                        LogChunkTrace(chunkCoord, $"ProcessPendingUpdates: ERROR applying density update: {e.Message}");
                                        // Continue with next update instead of breaking
                                    }
                                }
                                
                                if (anyDensityChanged)
                                {
                                    Debug.Log($"[ProcessPendingUpdates] Processed {updates.Count} updates for chunk {chunkCoord}, density changed");
                                    LogChunkTrace(chunkCoord, $"ProcessPendingUpdates: Processed {updates.Count} updates, density changed");
                                    modifiedChunks.Add(chunkCoord);
                                    
                                    // CRITICAL FIX: Ensure mesh update is queued after density changes
                                    if (chunk != null && chunks.ContainsKey(chunkCoord))
                                    {
                                        chunk.isMeshUpdateQueued = true;
                                        if (chunksNeedingMeshUpdate != null)
                                        {
                                            chunksNeedingMeshUpdate.Add(chunk);
                                            Debug.Log($"[ProcessPendingUpdates] Queued mesh update for chunk {chunkCoord} after density changes");
                                            LogChunkTrace(chunkCoord, $"ProcessPendingUpdates: Queued mesh update");
                                        }
                                    }
                                    
                                    // CRITICAL FIX: If this was a solid chunk, properly mark as modified
                                    if (isSolidChunk && chunkStateManager != null)
                                    {
                                        try
                                        {
                                            // Update terrain analysis immediately
                                            TerrainAnalysisCache.SaveAnalysis(chunkCoord, false, false, true);
                                            chunkStateManager.TryChangeState(
                                                chunkCoord,
                                                ChunkConfigurations.ChunkStatus.Modified,
                                                ChunkConfigurations.ChunkStateFlags.Active
                                            );
                                        }
                                        catch (System.Exception e)
                                        {
                                            Debug.LogError($"[ProcessPendingUpdates] Error updating state for solid chunk {chunkCoord}: {e.Message}");
                                        }
                                    }
                                }
                                else
                                {
                                    Debug.LogWarning($"[ProcessPendingUpdates] NONE of the {updates.Count} density updates for chunk {chunkCoord} changed density values");
                                }
                                updatesProcessed++;
                            }
                            else
                            {
                                Debug.LogWarning($"[ProcessPendingUpdates] Chunk {chunkCoord} has pending density updates but is not loaded!");
                                
                                // CRITICAL FIX: For solid chunks that don't exist yet, keep trying
                                if (isSolidChunk)
                                {
                                    try
                                    {
                                        // Ensure solid chunks are properly marked
                                        MarkSolidChunkForModification(chunkCoord);
                                        // Try to force load the chunk
                                        LoadChunkImmediately(chunkCoord);
                                        // Keep updates for next frame
                                    }
                                    catch (System.Exception e)
                                    {
                                        Debug.LogError($"[ProcessPendingUpdates] Error handling solid chunk {chunkCoord}: {e.Message}");
                                    }
                                }
                                else
                                {
                                    try
                                    {
                                        if (pendingDensityPointUpdates != null)
                                        {
                                            pendingDensityPointUpdates.Remove(chunkCoord);  // Remove if chunk doesn't exist
                                        }
                                    }
                                    catch (System.Exception e)
                                    {
                                        Debug.LogError($"[ProcessPendingUpdates] Error removing pending updates for chunk {chunkCoord}: {e.Message}");
                                    }
                                }
                            }
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogError($"[ProcessPendingUpdates] Error processing density updates for chunk {chunkCoord}: {e.Message}\n{e.StackTrace}");
                            // Continue with next chunk instead of breaking
                        }
                    }
                }

                // CRITICAL FIX: Always update and save modified chunks
                // CRITICAL: Check if ChunkStateManager is still available before processing modified chunks
                if (chunkStateManager != null)
                {
                    foreach (var chunkCoord in modifiedChunks)
                    {
                        try
                        {
                            if (chunks.TryGetValue(chunkCoord, out Chunk chunk) && chunk != null)
                            {
                                // Queue mesh update
                                chunk.isMeshUpdateQueued = true;
                                if (chunksNeedingMeshUpdate != null)
                                {
                                    chunksNeedingMeshUpdate.Add(chunk);
                                }
                                
                                // Force chunk to save
                                var chunkData = chunk.GetChunkData();
                                if (chunkData != null && chunkData.HasModifiedData)
                                {
                                    // Ensure TerrainAnalysisCache is invalidated
                                    TerrainAnalysisCache.InvalidateAnalysis(chunkCoord);
                                    // Save data
                                    chunkData.SaveData();
                                }
                                
                                // Update chunk state if needed
                                ChunkStateManager.ChunkState state;
                                try
                                {
                                    state = chunkStateManager.GetChunkState(chunkCoord);
                                    if (state.Status == ChunkConfigurations.ChunkStatus.Loaded)
                                    {
                                        chunkStateManager.TryChangeState(
                                            chunkCoord,
                                            ChunkConfigurations.ChunkStatus.Modified,
                                            ChunkConfigurations.ChunkStateFlags.Active
                                        );
                                    }
                                }
                                catch (System.Exception e)
                                {
                                    Debug.LogError($"[ProcessPendingUpdates] Error updating chunk state for {chunkCoord}: {e.Message}");
                                }
                            }
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogError($"[ProcessPendingUpdates] Error processing modified chunk {chunkCoord}: {e.Message}");
                        }
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ProcessPendingUpdates] CRITICAL ERROR: Unhandled exception in ProcessPendingUpdates: {e.Message}\n{e.StackTrace}");
        }
    }

    private void ClearPendingUpdates(Vector3Int chunkCoord)
    {
        lock (updateLock)
        {
            try
            {
                // Safe removal from dictionaries
                if (pendingVoxelUpdates.ContainsKey(chunkCoord))
                {
                    pendingVoxelUpdates.Remove(chunkCoord);
                    Debug.Log($"Forcefully cleared pending voxel updates for chunk {chunkCoord}");
                }

                if (pendingDensityPointUpdates.ContainsKey(chunkCoord))
                {
                    pendingDensityPointUpdates.Remove(chunkCoord);
                    Debug.Log($"Forcefully cleared pending density updates for chunk {chunkCoord}");
                }

                ClearForcedChunkFlag(chunkCoord, "Pending updates cleared");

                // Create a new HashSet without the chunk coordinate
                var newNeighborUpdates = new HashSet<Vector3Int>(
                    chunksWithPendingNeighborUpdates.Where(coord => coord != chunkCoord)
                );
                
                chunksWithPendingNeighborUpdates.Clear();
                foreach (var coord in newNeighborUpdates)
                {
                    chunksWithPendingNeighborUpdates.Add(coord);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error clearing pending updates for chunk {chunkCoord}: {e}");
            }
        }
    }

    private void ProcessMeshUpdates()
    {
        if (chunksNeedingMeshUpdate == null || chunksNeedingMeshUpdate.Count == 0) return;

        // Process mesh updates in batches
        if (MeshDataPool.Instance == null) return;
        int maxUpdatesPerFrame = MeshDataPool.Instance.GetDynamicChunksPerFrame();
        int updatesThisFrame = 0;
        var processedChunks = new List<Chunk>();

        foreach (var chunk in chunksNeedingMeshUpdate)
        {
            if (updatesThisFrame >= maxUpdatesPerFrame) break;

            Vector3Int chunkCoord = Coord.WorldToChunkCoord(chunk.transform.position, chunkSize, voxelSize);
            if (!chunk.gameObject.activeInHierarchy || chunk.generationCoroutine != null)
                continue;

            // CRITICAL FIX: Pass fullMesh=true to ensure the mesh actually regenerates from density data
            // When fullMesh=false and density data exists (e.g., from QuickCheck initialization),
            // Chunk.Generate applies an EMPTY mesh (0 vertices), leaving a hole in the terrain.
            chunk.Generate(log: false, fullMesh: true, quickCheck: false);
            chunk.isMeshUpdateQueued = false;
            processedChunks.Add(chunk);
            updatesThisFrame++;
        }

        // Remove processed chunks
        foreach (var chunk in processedChunks)
        {
            chunksNeedingMeshUpdate.Remove(chunk);
        }
    }

    public Vector3Int GetVoxelPositionFromHit(Vector3 hitPosition, Vector3 normal, bool isAdding)
    {
        float epsilon = voxelSize * Config.voxelEpsilon;
        Vector3 adjustedPosition = isAdding ? 
            hitPosition + normal * epsilon :
            hitPosition - normal * epsilon;

        Debug.Log($"Hit position: {hitPosition}, Adjusted: {adjustedPosition}");

        Vector3Int chunkCoord = Coord.WorldToChunkCoord(adjustedPosition, chunkSize, voxelSize);
        Debug.Log($"Calculated chunk coord: {chunkCoord}");
        Vector3 chunkPosition = Coord.GetWorldPosition(chunkCoord, Vector3Int.zero, chunkSize, voxelSize);
        Vector3Int voxelPos = Coord.WorldToVoxelCoord(adjustedPosition, chunkPosition, voxelSize);
        Debug.Log($"Initial voxel pos: {voxelPos}");

        // Add validation
        if (voxelPos.x < 0 || voxelPos.x >= chunkSize ||
            voxelPos.y < 0 || voxelPos.y >= chunkSize ||
            voxelPos.z < 0 || voxelPos.z >= chunkSize)
        {
            Debug.LogWarning($"Voxel position {voxelPos} out of bounds before clamping");
        }
        
        return voxelPos;
    }

    public void OnChunkGenerationComplete(Vector3Int chunkCoord)
    {
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
        
        // CRITICAL FIX: Only attempt valid state transitions based on current state
        // If in Loading state, transition to Loaded
        if (state.Status == ChunkConfigurations.ChunkStatus.Loading)
        {
            bool success = ChunkStateManager.Instance.TryChangeState(
                chunkCoord,
                ChunkConfigurations.ChunkStatus.Loaded,
                ChunkConfigurations.ChunkStateFlags.Active
            );
            
            if (!success)
            {
                Debug.LogError($"Failed to transition chunk {chunkCoord} from Loading to Loaded state");
            }
            else
            {
                // Remove from stuck chunk tracking since chunk successfully loaded
                chunkLoadingStartTime.Remove(chunkCoord);
            }
            
            // CRITICAL FIX: Check for pending updates IMMEDIATELY when chunk finishes loading
            // This ensures updates are applied as soon as possible
            bool hasPendingUpdates = HasPendingUpdates(chunkCoord);
            LogChunkTrace(chunkCoord, $"OnChunkGenerationComplete: Chunk finished loading, State: {state.Status}, HasPendingUpdates: {hasPendingUpdates}");
            if (hasPendingUpdates)
            {
                Debug.Log($"[OnChunkGenerationComplete] Chunk {chunkCoord} finished loading with pending updates - " +
                    $"VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}, " +
                    $"DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}");
                LogChunkTrace(chunkCoord, $"OnChunkGenerationComplete: Processing pending updates - VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}, DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}");
                
                // Process pending updates immediately for this chunk
                ProcessPendingUpdatesForChunk(chunkCoord);
            }
            
            // Check if this is an empty or solid chunk that can be unloaded after being loaded
            if (chunks.TryGetValue(chunkCoord, out Chunk chunk) && chunk.GetChunkData() != null)
            {
                var chunkData = chunk.GetChunkData();
                bool isEmpty = chunkData.IsEmptyChunk;
                bool isSolid = chunkData.IsSolidChunk;

                if ((isEmpty || isSolid) && !HasPendingUpdates(chunkCoord))
                {
                    if (initialLoadInProgress)
                    {
                        QueueInitialLoadEmptyChunk(chunkCoord);
                    }
                    else
                    {
                        ScheduleUnloadForEmptyOrSolidChunk(chunkCoord);
                    }
                }
            }
        }
        // If already in Loaded state, this might be a QuickCheck chunk - check if it can be unloaded
        // CRITICAL FIX: Also process pending updates if chunk is already in Loaded state
        // This handles the case where a chunk finishes loading but is already marked as Loaded
        else if (state.Status == ChunkConfigurations.ChunkStatus.Loaded)
        {
            // CRITICAL FIX: Process pending updates even if chunk is already in Loaded state
            // This ensures updates are applied even if the chunk was already loaded
            bool hasPendingUpdates = HasPendingUpdates(chunkCoord);
            if (hasPendingUpdates)
            {
                Debug.Log($"[OnChunkGenerationComplete] Chunk {chunkCoord} already in Loaded state but has pending updates - " +
                    $"VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}, " +
                    $"DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}");
                
                // Process pending updates immediately for this chunk
                ProcessPendingUpdatesForChunk(chunkCoord);
            }
            
            if (chunks.TryGetValue(chunkCoord, out Chunk chunk) && chunk.GetChunkData() != null)
            {
                var chunkData = chunk.GetChunkData();
                bool isEmpty = chunkData.IsEmptyChunk;
                bool isSolid = chunkData.IsSolidChunk;

                if ((isEmpty || isSolid) && !HasPendingUpdates(chunkCoord))
                {
                    if (initialLoadInProgress)
                    {
                        QueueInitialLoadEmptyChunk(chunkCoord);
                    }
                    else
                    {
                        ScheduleUnloadForEmptyOrSolidChunk(chunkCoord);
                    }
                }
            }
        }
        // If in Modified state, keep it there (don't try to change to Loaded)
        else if (state.Status == ChunkConfigurations.ChunkStatus.Modified)
        {
            Debug.Log($"Chunk {chunkCoord} generation complete but keeping Modified state");
        }
        // For any other state, log a warning
        else
        {
            Debug.LogWarning($"Chunk {chunkCoord} completed generation but is in unexpected state: {state.Status}");
        }

        HandleInitialLoadChunkReady(chunkCoord);

        if (EnhancedBenchmarkManager.Instance != null)
        {
            EnhancedBenchmarkManager.Instance.EndOperation(chunkCoord);
        }
    }

    public void ScheduleUnloadForEmptyOrSolidChunk(Vector3Int chunkCoord)
    {
        // Only schedule unload if the chunk is still loaded and doesn't have pending updates
        if (chunks.ContainsKey(chunkCoord) && !HasPendingUpdates(chunkCoord))
        {
            // Schedule with a short delay to allow any pending operations to complete
            StartCoroutine(DelayedUnloadForEmptyOrSolidChunk(chunkCoord, 0.5f));
        }
    }

    private IEnumerator DelayedUnloadForEmptyOrSolidChunk(Vector3Int chunkCoord, float delay)
    {
        yield return new WaitForSeconds(delay);
        
        // Double-check that unload is still appropriate
        if (chunks.ContainsKey(chunkCoord) && !HasPendingUpdates(chunkCoord))
        {
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            if (state.Status == ChunkConfigurations.ChunkStatus.Loaded || 
                state.Status == ChunkConfigurations.ChunkStatus.Modified)
            {
                if (config != null && config.enableChunkLifecycleLogs)
                {
                    Debug.Log($"Scheduling unload for empty/solid chunk {chunkCoord}");
                }
                operationsQueue.QueueChunkForUnload(chunkCoord);
            }
        }
    }

    public Chunk GetChunkAt(Vector3 globalPosition)
    {
        Vector3Int chunkCoordinates = Coord.WorldToChunkCoord(globalPosition, chunkSize, voxelSize);
        
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoordinates);
        if (chunks.TryGetValue(chunkCoordinates, out Chunk chunk))
        {
            // Allow interaction with both Loaded and Modified states
            if (state.Status == ChunkConfigurations.ChunkStatus.Loaded || 
                state.Status == ChunkConfigurations.ChunkStatus.Modified)
            {
                return chunk;
            }
            else
            {
                Debug.LogWarning($"Found chunk at {chunkCoordinates} but state is {state.Status}");
                return null;
            }
        }
        
        return null;
    }

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
        return hasPendingUpdates;
    }

    public void UpdateChunks(Vector3 playerPosition)
    {
        Vector3Int playerChunkCoordinates = Coord.WorldToChunkCoord(playerPosition, chunkSize, voxelSize);

        // Log player movement
        if (!playerChunkCoordinates.Equals(lastPlayerChunkCoordinates))
        {
            UpdateChunks(playerChunkCoordinates);
            lastPlayerChunkCoordinates = playerChunkCoordinates;
            justStarted = false;
        }
        else if (justStarted)
        {
            UpdateChunks(playerChunkCoordinates);
            lastPlayerChunkCoordinates = playerChunkCoordinates;
            justStarted = false;
        }
    }

    private void RefreshNearbyChunkAccessTimes()
    {
        if (chunks.Count == 0)
            return;

        if (Time.time - lastNearbyAccessRefreshTime < NearbyChunkAccessRefreshInterval)
            return;

        lastNearbyAccessRefreshTime = Time.time;

        accessTouchCenters.Clear();

        if (playerChunkCoordinates.Count > 0)
        {
            foreach (var entry in playerChunkCoordinates)
            {
                accessTouchCenters.Add(entry.Value);
            }
        }
        else if (lastPlayerChunkCoordinates != Vector3Int.zero)
        {
            accessTouchCenters.Add(lastPlayerChunkCoordinates);
        }

        if (accessTouchCenters.Count == 0)
            return;

        int verticalRadius = Mathf.Max(NearbyChunkVerticalRadius, Mathf.Max(1, verticalLoadRadius));

        foreach (var center in accessTouchCenters)
        {
            TouchChunksAround(center, NearbyChunkHorizontalRadius, verticalRadius);
        }
    }

    private void TouchChunksAround(Vector3Int center, int horizontalRadius, int verticalRadius)
    {
        for (int x = -horizontalRadius; x <= horizontalRadius; x++)
        for (int z = -horizontalRadius; z <= horizontalRadius; z++)
        for (int y = -verticalRadius; y <= verticalRadius; y++)
        {
            Vector3Int coord = center + new Vector3Int(x, y, z);

            if (chunks.TryGetValue(coord, out var chunk))
            {
                chunk.UpdateAccessTime();
            }
        }
    }

    private void UpdateChunks(Vector3Int centerChunkCoordinates)
    {
        if (ShouldLoadChunk(centerChunkCoordinates))
        {
            // Always perform a full load for the center chunk to avoid quick-check shortcuts
            operationsQueue.QueueChunkForLoad(centerChunkCoordinates, immediate: true, quickCheck: false);
            
            // Also load immediate neighbors
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
            {
                Vector3Int neighborCoord = centerChunkCoordinates + new Vector3Int(x, 0, z);
                if (ShouldLoadChunk(neighborCoord))
                {
                    operationsQueue.QueueChunkForLoad(neighborCoord, immediate: true, quickCheck: false);
                }
            }
        }

        int loadRadiusSquared = loadRadius * loadRadius;
        int unloadRadiusSquared = unloadRadius * unloadRadius;

        // Pre-calculate coordinate ranges
        int minX = centerChunkCoordinates.x - loadRadius;
        int maxX = centerChunkCoordinates.x + loadRadius;
        int minZ = centerChunkCoordinates.z - loadRadius;
        int maxZ = centerChunkCoordinates.z + loadRadius;
        int minY = centerChunkCoordinates.y - verticalLoadRadius;
        int maxY = centerChunkCoordinates.y + verticalLoadRadius;

        // Collect and sort chunks by distance
        List<ChunkLoadRequest> loadRequests = new List<ChunkLoadRequest>();
        int consideredChunks = 0;
        int validLoadRequests = 0;

        for (int x = minX; x <= maxX; x++)
        {
            int dx = x - centerChunkCoordinates.x;
            int dxSquared = dx * dx;
            
            for (int z = minZ; z <= maxZ; z++)
            {
                int dz = z - centerChunkCoordinates.z;
                int distanceSquared = dxSquared + dz * dz;

                if (distanceSquared <= loadRadiusSquared)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        consideredChunks++;
                        Vector3Int chunkCoord = new Vector3Int(x, y, z);
                        if (ShouldLoadChunk(chunkCoord))
                        {
                            float distance = Mathf.Sqrt(distanceSquared + y * y);
                            loadRequests.Add(new ChunkLoadRequest(chunkCoord, distance));
                            validLoadRequests++;
                        }
                    }
                }
            }
        }

        // Sort by distance from center
        loadRequests.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        // Queue chunks in distance order
        foreach (var request in loadRequests)
        {
            bool immediate = request.Distance <= ImmediateLoadDistanceThreshold;
            
            operationsQueue.QueueChunkForLoad(request.Coordinate, immediate, quickCheck: !immediate);
        }

        // Process unloading
        ProcessChunkUnloading(centerChunkCoordinates);
    }

    private void ProcessChunkUnloading(Vector3Int centerChunkCoordinates)
    {
        int unloadRadiusSquared = unloadRadius * unloadRadius;
        List<Vector3Int> chunksToUnload = new List<Vector3Int>();

        foreach (var chunkEntry in chunks)
        {
            Vector3Int chunkCoord = chunkEntry.Key;
            int dx = chunkCoord.x - centerChunkCoordinates.x;
            int dz = chunkCoord.z - centerChunkCoordinates.z;
            int distanceSquared = dx * dx + dz * dz;
            int verticalDistance = Mathf.Abs(chunkCoord.y - centerChunkCoordinates.y);

            if (distanceSquared > unloadRadiusSquared || verticalDistance > verticalUnloadRadius)
            {
                if (!HasPendingUpdates(chunkCoord))
                {
                    // Important: Check current state before queueing unload
                    var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                    if (state.Status == ChunkConfigurations.ChunkStatus.Loaded || 
                        state.Status == ChunkConfigurations.ChunkStatus.Modified)
                    {
                        chunksToUnload.Add(chunkCoord);
                    }
                }
            }
        }

        // Queue all unloads without limiting
        foreach (var chunkCoord in chunksToUnload)
        {
            operationsQueue.QueueChunkForUnload(chunkCoord);
        }
    }

    private bool ShouldLoadChunk(Vector3Int chunkCoord)
    {
        // Skip if already being validated
        if (chunksBeingValidated.Contains(chunkCoord))
            return false;

        try 
        {
            chunksBeingValidated.Add(chunkCoord);

            // 1. Quick rejection checks
            
            // Skip if already loaded
            if (chunks.ContainsKey(chunkCoord))
            {
                return false;
            }

            // Get current chunk state
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            
            // 2. Check for conditions that should ALWAYS force a load (even if quarantined)
            
            // CRITICAL: Always load solid chunks that are marked for modification
            if (modifiedSolidChunks.Contains(chunkCoord))
            {
                if (ChunkLifecycleLogsEnabled)
                {
                    Debug.Log($"[ShouldLoadChunk] Forcing load of modified solid chunk {chunkCoord}");
                }
                loadValidationCache[chunkCoord] = Time.time;
                return true;
            }

            // CRITICAL: Check if the TerrainAnalysisCache has marked this chunk as modified
            if (TerrainAnalysisCache.IsChunkTrackedAsModified(chunkCoord))
            {
                if (ChunkLifecycleLogsEnabled)
                {
                    Debug.Log($"[ShouldLoadChunk] Forcing load of chunk {chunkCoord} marked as modified in TerrainAnalysisCache");
                }
                loadValidationCache[chunkCoord] = Time.time;
                return true;
            }

            // CRITICAL FIX: Chunks with pending updates should always load, EVEN IF QUARANTINED
            // This allows quarantined chunks to recover and apply their modifications
            if (HasPendingUpdates(chunkCoord))
            {
                bool isQuarantined = ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord);
                if (isQuarantined)
                {
                    Debug.LogWarning($"[ShouldLoadChunk] FORCING load of QUARANTINED chunk {chunkCoord} with pending updates - " +
                        $"VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}, " +
                        $"DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}");
                    LogChunkTrace(chunkCoord, $"ShouldLoadChunk: FORCING load of QUARANTINED chunk due to pending updates - VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}, DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}");
                }
                else
                {
                    // DEBUG: Always log this, not just when ChunkLifecycleLogsEnabled
                    Debug.Log($"[ShouldLoadChunk] FORCING load of chunk {chunkCoord} with pending updates - " +
                        $"VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}, " +
                        $"DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}");
                    LogChunkTrace(chunkCoord, $"ShouldLoadChunk: FORCING load due to pending updates - VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}, DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}");
                }
                loadValidationCache[chunkCoord] = Time.time;
                return true;
            }
            
            // Skip if quarantined (only if no pending updates)
            if (ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord))
            {
                loadValidationCache[chunkCoord] = Time.time;
                return false;
            }

            // 3. Check basic state requirements
            
            // CRITICAL FIX: If chunk has pending updates, allow loading even from Error state
            // This ensures stuck chunks can be reloaded when they have pending updates
            bool hasPending = HasPendingUpdates(chunkCoord);
            bool isMarkedForMod = modifiedSolidChunks.Contains(chunkCoord);
            
            // Allow loading if chunk is None or Unloaded
            bool validState = (state.Status == ChunkConfigurations.ChunkStatus.None || 
                            state.Status == ChunkConfigurations.ChunkStatus.Unloaded);
            
            // CRITICAL: Override invalid state if chunk has pending updates or is marked for modification
            if (!validState && (hasPending || isMarkedForMod))
            {
                Debug.LogWarning($"[ShouldLoadChunk] Chunk {chunkCoord} in invalid state ({state.Status}) but has pending updates - " +
                    $"forcing reload. HasPending: {hasPending}, IsMarkedForMod: {isMarkedForMod}");
                // Force state to Unloaded so it can be reloaded
                if (state.Status == ChunkConfigurations.ChunkStatus.Error || 
                    state.Status == ChunkConfigurations.ChunkStatus.Loading)
                {
                    ChunkStateManager.Instance.TryChangeState(
                        chunkCoord,
                        ChunkConfigurations.ChunkStatus.Unloaded,
                        ChunkConfigurations.ChunkStateFlags.None
                    );
                    validState = true;
                }
            }
            
            if (!validState)
            {
                Debug.Log($"[ShouldLoadChunk] Chunk {chunkCoord} in invalid state for loading: {state.Status}");
                LogChunkTrace(chunkCoord, $"ShouldLoadChunk: REJECTED - invalid state: {state.Status}");
                return false;
            }

            // Check for pending load operations
            if (operationsQueue.HasPendingLoadOperation(chunkCoord))
            {
                if (ChunkLifecycleLogsEnabled)
                {
                    Debug.Log($"[ShouldLoadChunk] Chunk {chunkCoord} already has a pending load operation");
                }
                return false;
            }

            // 4. Check TerrainAnalysisCache to skip empty or solid chunks
            if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out TerrainAnalysisData analysis))
            {
                // Always load modified chunks regardless of whether they're solid/empty
                if (analysis.WasModified)
                {
                    Debug.Log($"[ShouldLoadChunk] Forcing load of chunk {chunkCoord} marked as modified in analysis cache");
                    loadValidationCache[chunkCoord] = Time.time;
                    return true;
                }
                
                // CRITICAL FIX: Double-check for pending updates BEFORE skipping
                // This prevents race conditions where cache check happens before updates are registered
                // (hasPending and isMarkedForMod already declared above)
                
                // CRITICAL FIX: Only skip solid/empty chunks if they're ALSO far from all players
                // If a solid chunk is within the active load radius, it needs to be loaded for
                // player interaction (mining), collision, and proper rendering of adjacent chunks
                if ((analysis.IsEmpty || analysis.IsSolid) && 
                    !isMarkedForMod && 
                    !hasPending)
                {
                    // Calculate minimum distance to any player
                    float minDistanceToPlayers = float.MaxValue;
                    foreach (var playerEntry in playerChunkCoordinates)
                    {
                        Vector3Int playerChunk = playerEntry.Value;
                        float distance = Vector3Int.Distance(chunkCoord, playerChunk);
                        minDistanceToPlayers = Mathf.Min(minDistanceToPlayers, distance);
                    }
                    
                    // Only skip if the chunk is far from all players (beyond vertical load radius)
                    // This allows players to mine into solid chunks when they get close
                    if (minDistanceToPlayers > verticalLoadRadius * 1.5f)
                    {
                        Debug.Log($"[ShouldLoadChunk] Skipping load of {(analysis.IsEmpty ? "empty" : "solid")} chunk {chunkCoord} - " +
                            $"distance: {minDistanceToPlayers:F2}, HasPendingUpdates: {hasPending}, IsMarkedForMod: {isMarkedForMod}");
                        LogChunkTrace(chunkCoord, $"ShouldLoadChunk: SKIPPED - {(analysis.IsEmpty ? "empty" : "solid")} chunk too far, distance: {minDistanceToPlayers:F2}");
                        loadValidationCache[chunkCoord] = Time.time;
                        return false;
                    }
                    else
                    {
                        Debug.Log($"[ShouldLoadChunk] Loading {(analysis.IsEmpty ? "empty" : "solid")} chunk {chunkCoord} - close to player (distance: {minDistanceToPlayers:F2})");
                    }
                }
                
                // If we have pending updates or are marked for mod, force load
                if (hasPending || isMarkedForMod)
                {
                    Debug.Log($"[ShouldLoadChunk] Overriding skip for {(analysis.IsEmpty ? "empty" : "solid")} chunk {chunkCoord} - " +
                        $"HasPendingUpdates: {hasPending}, IsMarkedForMod: {isMarkedForMod}");
                    loadValidationCache[chunkCoord] = Time.time;
                    return true;
                }
            }

            loadValidationCache[chunkCoord] = Time.time;
            return true;
        }
        finally 
        {
            chunksBeingValidated.Remove(chunkCoord);
        }
    }

    public void MarkSolidChunkForModification(Vector3Int chunkCoord)
    {
        // Skip if already marked
        if (modifiedSolidChunks.Contains(chunkCoord))
            return;

        Debug.Log($"Marking solid chunk {chunkCoord} for modification");
        modifiedSolidChunks.Add(chunkCoord);
        
        // CRITICAL FIX: Create a new analysis entry explicitly marking this chunk as modified
        TerrainAnalysisCache.SaveAnalysis(chunkCoord, false, false, true);
        TerrainAnalysisCache.ProcessPendingSavesImmediate(1);
        
        // Also invalidate the terrain analysis for this chunk so future loads recalculate
        TerrainAnalysisCache.InvalidateAnalysis(chunkCoord);
        
        // Remove from validation cache to force re-evaluation
        loadValidationCache.Remove(chunkCoord);
        
        // Ensure this chunk gets high priority in the load queue
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
        if (state.Status == ChunkConfigurations.ChunkStatus.None || 
            state.Status == ChunkConfigurations.ChunkStatus.Unloaded)
        {
            // Direct call to avoid any filtering that might occur in RequestChunkLoad
            operationsQueue.QueueChunkForLoad(chunkCoord, immediate: true, quickCheck: false);
        }
    }

    private void MarkChunkForForcedLoad(Vector3Int chunkCoord, string reason = null)
    {
        lock (updateLock)
        {
            if (forcedBoundaryChunks.Add(chunkCoord))
            {
                string logMessage = reason != null
                    ? $"[ForcedChunk] Marked {chunkCoord} for forced load ({reason})"
                    : $"[ForcedChunk] Marked {chunkCoord} for forced load";
                Debug.Log(logMessage);
                LogChunkTrace(chunkCoord, $"Forced load flag set{(reason != null ? $" - {reason}" : string.Empty)}");
            }
        }
    }

    private void ClearForcedChunkFlag(Vector3Int chunkCoord, string reason = null)
    {
        lock (updateLock)
        {
            if (forcedBoundaryChunks.Remove(chunkCoord))
            {
                string logMessage = reason != null
                    ? $"[ForcedChunk] Cleared forced load flag for {chunkCoord} ({reason})"
                    : $"[ForcedChunk] Cleared forced load flag for {chunkCoord}";
                Debug.Log(logMessage);
                LogChunkTrace(chunkCoord, $"Forced load flag cleared{(reason != null ? $" - {reason}" : string.Empty)}");
            }
        }
    }

    public bool IsChunkForcedForLoad(Vector3Int chunkCoord)
    {
        lock (updateLock)
        {
            return forcedBoundaryChunks.Contains(chunkCoord);
        }
    }

    private void CleanupValidationCache()
    {
        if (Time.frameCount % 100 == 0) // Every 100 frames
        {
            float currentTime = Time.time;
            var keysToRemove = loadValidationCache
                .Where(kvp => currentTime - kvp.Value > VALIDATION_CACHE_TIME)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                loadValidationCache.Remove(key);
            }
        }
    }
    
    public void RequestImmediateChunkLoad(Vector3Int chunkCoord)
    {
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
        bool isQuarantined = ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord);
        bool hasPendingUpdates = HasPendingUpdates(chunkCoord);
        
        // DEBUG: Log immediate load request
        Debug.Log($"[RequestImmediateChunkLoad] Chunk {chunkCoord} - " +
            $"State: {state.Status}, Quarantined: {isQuarantined}, HasPendingUpdates: {hasPendingUpdates}");
        LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad called - State: {state.Status}, Quarantined: {isQuarantined}, HasPendingUpdates: {hasPendingUpdates}");
        
        // CRITICAL FIX: Allow loading quarantined chunks if they have pending updates
        // This enables recovery of stuck chunks that have modifications queued
        if (isQuarantined && !hasPendingUpdates)
        {
            Debug.LogWarning($"[RequestImmediateChunkLoad] Chunk {chunkCoord} is QUARANTINED with no pending updates - cannot load!");
            LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad: Skipped - quarantined with no pending updates");
            return;
        }
        
        if (isQuarantined && hasPendingUpdates)
        {
            Debug.LogWarning($"[RequestImmediateChunkLoad] Allowing load request for QUARANTINED chunk {chunkCoord} because it has pending updates");
            LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad: Allowing quarantined chunk load - has pending updates");
        }
        
        // CRITICAL FIX: Handle chunks in Unloading state
        // If chunk is unloading, we need to wait for it to finish or cancel the unload
        if (state.Status == ChunkConfigurations.ChunkStatus.Unloading)
        {
            Debug.LogWarning($"[RequestImmediateChunkLoad] Chunk {chunkCoord} is currently UNLOADING - " +
                $"HasPendingUpdates: {hasPendingUpdates}, IsMarkedForMod: {modifiedSolidChunks.Contains(chunkCoord)}");
            LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad: Chunk is UNLOADING");
            
            // If chunk has pending updates or is marked for modification, cancel the unload
            // by forcing state to Unloaded so it can be reloaded
            if (hasPendingUpdates || modifiedSolidChunks.Contains(chunkCoord))
            {
                Debug.LogWarning($"[RequestImmediateChunkLoad] Cancelling unload for chunk {chunkCoord} due to pending updates/modifications");
                LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad: Cancelling unload due to pending updates");
                
                // Try to transition Unloading -> Unloaded -> None so we can reload
                if (ChunkStateManager.Instance.TryChangeState(
                    chunkCoord,
                    ChunkConfigurations.ChunkStatus.Unloaded,
                    ChunkConfigurations.ChunkStateFlags.None))
                {
                    state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                    Debug.Log($"[RequestImmediateChunkLoad] Successfully cancelled unload for chunk {chunkCoord}, new state: {state.Status}");
                    LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad: Successfully cancelled unload");
                }
                else
                {
                    Debug.LogError($"[RequestImmediateChunkLoad] Failed to cancel unload for chunk {chunkCoord}");
                    LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad: FAILED to cancel unload");
                    return;
                }
            }
            else
            {
                // No pending updates, let unload complete
                Debug.Log($"[RequestImmediateChunkLoad] Chunk {chunkCoord} is unloading with no pending updates - waiting for unload to complete");
                LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad: Waiting for unload to complete");
                return;
            }
        }
        
        // If chunk exists but is in wrong state, force unload first
        if (chunks.ContainsKey(chunkCoord))
        {
            Debug.LogWarning($"[RequestImmediateChunkLoad] Chunk {chunkCoord} already exists but in wrong state - requesting unload");
            LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad: Chunk exists in wrong state, requesting unload");
            operationsQueue.QueueChunkForUnload(chunkCoord);
            return; // The unload operation will trigger a reload when complete
        }

        // Only queue if in valid state for loading
        if (state.Status == ChunkConfigurations.ChunkStatus.None ||
            state.Status == ChunkConfigurations.ChunkStatus.Unloaded ||
            state.Status == ChunkConfigurations.ChunkStatus.Error) // Allow loading from Error state for recovery
        {
            Debug.Log($"[RequestImmediateChunkLoad] Queuing chunk {chunkCoord} for immediate load (no quickCheck)");
            LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad: Queuing for immediate load");
            
            // CRITICAL FIX: Add exception handling around QueueChunkForLoad
            try
            {
                operationsQueue.QueueChunkForLoad(chunkCoord, immediate: true, quickCheck: false);
            }
            catch (Exception e)
            {
                Debug.LogError($"[RequestImmediateChunkLoad] Failed to queue chunk {chunkCoord} for load: {e.Message}\n{e.StackTrace}");
                LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad: ERROR - Failed to queue: {e.Message}");
                OnChunkLoadFailed(chunkCoord, $"QueueChunkForLoad failed: {e.Message}");
            }
        }
        else
        {
            Debug.LogWarning($"[RequestImmediateChunkLoad] Cannot load chunk {chunkCoord} - invalid state: {state.Status}");
            LogChunkTrace(chunkCoord, $"RequestImmediateChunkLoad: Cannot load - invalid state: {state.Status}");
        }
    }

    // When a chunk is unloaded
    public void UnregisterChunk(Vector3Int chunkCoord)
    {
        activeChunks.Remove(chunkCoord);
    }

    public void OnChunkLoadFailed(Vector3Int chunkCoord, string reason)
    {
        Debug.LogError($"Chunk load failed for {chunkCoord}: {reason}");
        
        // Clear any pending operations
        if (chunks.TryGetValue(chunkCoord, out Chunk chunk))
        {
            chunk.CompleteAllJobs();
            ClearPendingUpdates(chunkCoord);
        }

        if (initialLoadPending.Remove(chunkCoord))
        {
            Debug.LogWarning($"Removing chunk {chunkCoord} from initial load pending set due to failure.");
            UpdateInitialLoadProgressState();
        }

        QuarantineChunk(chunkCoord, 
            $"Operation Load failed to complete: {reason}", 
            ChunkConfigurations.ChunkStatus.None);
    }

    public void OnChunkLoadSucceeded(Vector3Int chunkCoord)
    {
        chunkLoadAttempts.Remove(chunkCoord);
        
        // Process any pending updates that were waiting for this chunk
        if (pendingVoxelUpdates.ContainsKey(chunkCoord) || 
            pendingDensityPointUpdates.ContainsKey(chunkCoord))
        {
            ProcessPendingUpdates();
        }
    }

    private void ProcessQuarantinedChunks()
    {
        if (ChunkStateManager.Instance.QuarantinedChunks.Count == 0) return;

        var processedChunks = new List<Vector3Int>();
        var currentMemoryPressure = (float)MeshDataPool.Instance.GetCurrentMemoryUsage() / 
                                World.Instance.Config.MaxMeshCacheSize;
        
        // Only attempt recovery if memory pressure isn't too high
        if (currentMemoryPressure < 0.8f)
        {
            foreach (var chunkCoord in ChunkStateManager.Instance.QuarantinedChunks)
            {
                var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                var errorHistory = ChunkStateManager.Instance.GetErrorHistory(chunkCoord);
                
                Debug.LogWarning($"Attempting recovery for quarantined chunk {chunkCoord}:\n" +
                            $"Last Status: {state.Status}\n" +
                            $"Error Count: {errorHistory.Count}\n" //+
                            //$"Last Error: {(errorHistory.Count > 0 ? errorHistory[errorHistory.Count - 1].Message : "None")}"
                );

                bool recovered = AttemptChunkRecovery(chunkCoord);
                if (recovered)
                {
                    processedChunks.Add(chunkCoord);
                    Debug.Log($"Successfully recovered chunk {chunkCoord}");
                }
            }
        }

        // Remove recovered chunks from quarantine
        foreach (var chunk in processedChunks)
        {
            ChunkStateManager.Instance.QuarantinedChunks.Remove(chunk);
        }
    }

    /// <summary>
    /// Detects chunks stuck in Loading state and attempts to recover them.
    /// This addresses the issue where chunks can get stuck if generation fails or is cancelled.
    /// </summary>
    private void DetectAndRecoverStuckChunks()
    {
        if (ChunkStateManager.Instance == null) return;
        
        float currentTime = Time.time;
        var stuckChunks = new List<Vector3Int>();
        
        // Check all chunks that are currently in Loading state
        // We need to check chunks that are tracked in chunkLoadingStartTime
        var chunksToCheck = new List<Vector3Int>(chunkLoadingStartTime.Keys);
        
        foreach (var chunkCoord in chunksToCheck)
        {
            // Skip if chunk is already loaded
            if (chunks.ContainsKey(chunkCoord))
            {
                chunkLoadingStartTime.Remove(chunkCoord);
                continue;
            }
            
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            
            // If chunk is no longer in Loading state, remove from tracking
            if (state.Status != ChunkConfigurations.ChunkStatus.Loading)
            {
                chunkLoadingStartTime.Remove(chunkCoord);
                continue;
            }
            
            // Check if chunk has been in Loading state too long
            if (chunkLoadingStartTime.TryGetValue(chunkCoord, out float startTime))
            {
                float timeInLoading = currentTime - startTime;
                if (timeInLoading > STUCK_CHUNK_TIMEOUT)
                {
                    stuckChunks.Add(chunkCoord);
                    Debug.LogWarning($"[DetectAndRecoverStuckChunks] Chunk {chunkCoord} stuck in Loading state for {timeInLoading:F2} seconds - " +
                        $"HasPendingUpdates: {HasPendingUpdates(chunkCoord)}, " +
                        $"Quarantined: {ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord)}");
                    LogChunkTrace(chunkCoord, $"DetectAndRecoverStuckChunks: Chunk stuck in Loading for {timeInLoading:F2}s");
                }
            }
        }
        
        // Attempt recovery for stuck chunks
        foreach (var chunkCoord in stuckChunks)
        {
            bool hasPendingUpdates = HasPendingUpdates(chunkCoord);
            bool isQuarantined = ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord);
            
            Debug.LogWarning($"[DetectAndRecoverStuckChunks] Attempting recovery for stuck chunk {chunkCoord} - " +
                $"HasPendingUpdates: {hasPendingUpdates}, Quarantined: {isQuarantined}");
            LogChunkTrace(chunkCoord, $"DetectAndRecoverStuckChunks: Attempting recovery - HasPendingUpdates: {hasPendingUpdates}, Quarantined: {isQuarantined}");
            
            // If chunk has pending updates, it's critical to recover it
            // Quarantine it first if not already quarantined, then attempt recovery
            if (!isQuarantined)
            {
                QuarantineChunk(chunkCoord, $"Stuck in Loading state for {currentTime - chunkLoadingStartTime[chunkCoord]:F2} seconds", 
                    ChunkConfigurations.ChunkStatus.Loading);
            }
            
            // Attempt recovery (will handle both Error and Loading states)
            bool recovered = AttemptChunkRecovery(chunkCoord);
            if (recovered)
            {
                chunkLoadingStartTime.Remove(chunkCoord);
                Debug.Log($"[DetectAndRecoverStuckChunks] Successfully recovered stuck chunk {chunkCoord}");
                LogChunkTrace(chunkCoord, $"DetectAndRecoverStuckChunks: Successfully recovered");
            }
        }
    }

    public void QuarantineChunk(Vector3Int chunkCoord, string reason, ChunkConfigurations.ChunkStatus lastStatus)
    {
        var error = new ChunkConfigurations.ChunkError
        {
            Message = reason,
            Status = lastStatus,
            Timestamp = DateTime.UtcNow,
            RetryCount = ChunkStateManager.Instance.GetErrorHistory(chunkCoord).Count
        };

        ChunkStateManager.Instance.LogChunkError(chunkCoord, error);
        ChunkStateManager.Instance.QuarantinedChunks.Add(chunkCoord);

        var currentState = ChunkStateManager.Instance.GetChunkState(chunkCoord);
        if (currentState.Status != ChunkConfigurations.ChunkStatus.Error)
        {
            ChunkStateManager.Instance.TryChangeState(
                chunkCoord,
                ChunkConfigurations.ChunkStatus.Error,
                ChunkConfigurations.ChunkStateFlags.Error);
        }

        // Clear any pending operations
        if (chunks.TryGetValue(chunkCoord, out Chunk chunk))
        {
            chunk.CompleteAllJobs();
        }

        // CRITICAL FIX: Preserve pending updates if they exist - don't clear them!
        // This allows chunks to recover and still apply their modifications
        bool hasPendingUpdates = HasPendingUpdates(chunkCoord);
        if (hasPendingUpdates)
        {
            Debug.LogWarning($"[QuarantineChunk] Chunk {chunkCoord} quarantined but PRESERVING pending updates - " +
                $"VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}, " +
                $"DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}");
            LogChunkTrace(chunkCoord, $"QuarantineChunk: PRESERVING pending updates - VoxelUpdates: {pendingVoxelUpdates.ContainsKey(chunkCoord)}, DensityUpdates: {pendingDensityPointUpdates.ContainsKey(chunkCoord)}");
        }
        else
        {
            // Only clear updates if there are none - this prevents unnecessary data loss
            ClearPendingUpdates(chunkCoord);
        }
        
        Debug.LogWarning($"Chunk {chunkCoord} quarantined. Reason: {reason}, Last Status: {lastStatus}, PreservedUpdates: {hasPendingUpdates}");
    }

    private bool AttemptChunkRecovery(Vector3Int chunkCoord)
    {
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
        
        // CRITICAL FIX: Also recover chunks stuck in Loading state
        // Chunks can get stuck in Loading if generation fails or is cancelled
        bool isStuckInLoading = state.Status == ChunkConfigurations.ChunkStatus.Loading;
        bool isInError = state.Status == ChunkConfigurations.ChunkStatus.Error;
        
        if (isInError || isStuckInLoading)
        {
            Debug.LogWarning($"[AttemptChunkRecovery] Attempting recovery for chunk {chunkCoord} - " +
                $"State: {state.Status}, HasPendingUpdates: {HasPendingUpdates(chunkCoord)}");
            LogChunkTrace(chunkCoord, $"AttemptChunkRecovery: Starting recovery from {state.Status}");
            
            // CRITICAL FIX: If chunk is stuck in Loading, we need to clean up any existing chunk object first
            // The chunk object might still exist with a running coroutine, preventing proper recovery
            if (isStuckInLoading && chunks.TryGetValue(chunkCoord, out Chunk stuckChunk))
            {
                Debug.LogWarning($"[AttemptChunkRecovery] Found stuck chunk object for {chunkCoord} - cleaning up");
                LogChunkTrace(chunkCoord, $"AttemptChunkRecovery: Found stuck chunk object, cleaning up");
                
                try
                {
                    // Stop any running coroutines and complete jobs
                    stuckChunk.CompleteAllJobs();
                    
                    // Return chunk to pool if it's still valid
                    if (stuckChunk != null)
                    {
                        ChunkPoolManager.Instance.ReturnChunk(stuckChunk);
                    }
                    
                    // Remove from chunks dictionary
                    chunks.Remove(chunkCoord);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[AttemptChunkRecovery] Error cleaning up stuck chunk object for {chunkCoord}: {e.Message}");
                    LogChunkTrace(chunkCoord, $"AttemptChunkRecovery: ERROR cleaning up stuck chunk object: {e.Message}");
                }
            }
            
            // Reset chunk state to None so it can be reloaded
            ChunkConfigurations.ChunkStatus targetState = ChunkConfigurations.ChunkStatus.None;
            
            // CRITICAL FIX: Loading -> Unloaded is NOT a valid transition!
            // We must use Loading -> Error -> None instead
            if (isStuckInLoading)
            {
                // Transition Loading -> Error (Error can always be set)
                if (ChunkStateManager.Instance.TryChangeState(
                    chunkCoord, 
                    ChunkConfigurations.ChunkStatus.Error,
                    ChunkConfigurations.ChunkStateFlags.Error))
                {
                    LogChunkTrace(chunkCoord, $"AttemptChunkRecovery: Transitioned Loading -> Error");
                    
                    // Now transition Error -> None (valid recovery path)
                    if (ChunkStateManager.Instance.TryChangeState(
                        chunkCoord, 
                        ChunkConfigurations.ChunkStatus.None,
                        ChunkConfigurations.ChunkStateFlags.None))
                    {
                        targetState = ChunkConfigurations.ChunkStatus.None;
                        LogChunkTrace(chunkCoord, $"AttemptChunkRecovery: Transitioned Error -> None");
                    }
                    else
                    {
                        Debug.LogError($"[AttemptChunkRecovery] Failed to transition Error -> None for chunk {chunkCoord}");
                        LogChunkTrace(chunkCoord, $"AttemptChunkRecovery: FAILED to transition Error -> None");
                    }
                }
                else
                {
                    Debug.LogError($"[AttemptChunkRecovery] Failed to transition Loading -> Error for chunk {chunkCoord}");
                    LogChunkTrace(chunkCoord, $"AttemptChunkRecovery: FAILED to transition Loading -> Error");
                }
            }
            else if (isInError)
            {
                // Reset chunk state from Error -> None
                if (ChunkStateManager.Instance.TryChangeState(
                    chunkCoord, 
                    ChunkConfigurations.ChunkStatus.None,
                    ChunkConfigurations.ChunkStateFlags.None))
                {
                    targetState = ChunkConfigurations.ChunkStatus.None;
                    LogChunkTrace(chunkCoord, $"AttemptChunkRecovery: Transitioned Error -> None");
                }
                else
                {
                    Debug.LogError($"[AttemptChunkRecovery] Failed to transition Error -> None for chunk {chunkCoord}");
                    LogChunkTrace(chunkCoord, $"AttemptChunkRecovery: FAILED to transition Error -> None");
                }
            }
            
            // If we successfully reset the state, request a new load
            if (targetState == ChunkConfigurations.ChunkStatus.None)
            {
                // Remove from quarantine if recovery succeeds
                ChunkStateManager.Instance.QuarantinedChunks.Remove(chunkCoord);
                
                // Clear stuck chunk tracking
                chunkLoadingStartTime.Remove(chunkCoord);
                
                // Request new load (disable quickCheck to ensure full generation)
                operationsQueue.QueueChunkForLoad(chunkCoord, immediate: true, quickCheck: false);
                Debug.Log($"[AttemptChunkRecovery] Successfully recovered chunk {chunkCoord} from {state.Status} to {targetState}");
                LogChunkTrace(chunkCoord, $"AttemptChunkRecovery: Successfully recovered, requesting new load");
                return true;
            }
            else
            {
                Debug.LogError($"[AttemptChunkRecovery] Failed to recover chunk {chunkCoord} - could not reset state");
                LogChunkTrace(chunkCoord, $"AttemptChunkRecovery: FAILED - could not reset state");
            }
        }
        return false;
    }

    public void LogChunkProcessingState()
    {
        int activeChunks = chunks.Count;
        int quarantinedChunks = ChunkStateManager.Instance.QuarantinedChunks.Count;
        int pendingUpdates = pendingVoxelUpdates.Count + pendingDensityPointUpdates.Count;
        float memoryPressure = (float)MeshDataPool.Instance.GetCurrentMemoryUsage() / 
                            Config.MaxMeshCacheSize;

        Debug.Log($"Chunk Processing State:\n" +
                $"Active Chunks: {activeChunks}\n" +
                $"Quarantined: {quarantinedChunks}\n" +
                $"Pending Updates: {pendingUpdates}\n" +
                $"Memory Pressure: {memoryPressure:P2}\n" +
                $"Load Attempts Tracked: {chunkLoadAttempts.Count}");
    }
    #region 
    public void HandleDisconnectedClientChunk(Vector3Int chunkCoord)
    {
        // Clean up chunk when owning client disconnects
        if (chunks.TryGetValue(chunkCoord, out Chunk chunk))
        {
            operationsQueue.QueueChunkForUnload(chunkCoord);
        }
    }

    // Diagnostic: Check if this chunk matches the problematic pattern (|X|=5 or |Z|=5 at Y=-1)
    private bool IsProblematicCoordinate(Vector3Int chunkCoord)
    {
        return chunkCoord.y == -1 && (Mathf.Abs(chunkCoord.x) == 5 || Mathf.Abs(chunkCoord.z) == 5);
    }

    public Vector3 GetChunkWorldPosition(Vector3Int chunkCoord)
    {
        Vector3 position = new Vector3(
            chunkCoord.x * chunkSize * voxelSize,
            chunkCoord.y * chunkSize * voxelSize,
            chunkCoord.z * chunkSize * voxelSize
        );
        
        // DIAGNOSTIC: Log for problematic coordinates
        if (IsProblematicCoordinate(chunkCoord))
        {
            Debug.Log($"[COORD_5_DEBUG] GetChunkWorldPosition({chunkCoord}) = {position}");
        }
        
        return position;
    }

    public void ApplyTerrainModification(Vector3Int chunkCoord, Vector3Int voxelPos, bool isAdding)
    {
        if (chunks.TryGetValue(chunkCoord, out Chunk chunk))
        {
            try
            {
                // Ensure a valid chunk state before modification
                var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                if (state.Status != ChunkConfigurations.ChunkStatus.Loaded && 
                    state.Status != ChunkConfigurations.ChunkStatus.Modified)
                {
                    Debug.LogWarning($"Cannot apply modification to chunk {chunkCoord} in state {state.Status}");
                    QueueVoxelUpdate(chunkCoord, voxelPos, isAdding, true);
                    return;
                }
                
                // Apply the modification directly
                if (isAdding)
                    chunk.AddVoxel(voxelPos);
                else
                    chunk.DamageVoxel(voxelPos, 1);
                
                // If we were in Loaded state, transition to Modified
                if (state.Status == ChunkConfigurations.ChunkStatus.Loaded)
                {
                    ChunkStateManager.Instance.TryChangeState(
                        chunkCoord,
                        ChunkConfigurations.ChunkStatus.Modified,
                        ChunkConfigurations.ChunkStateFlags.Active
                    );
                }
                
                // Make sure to invalidate the terrain analysis cache
                TerrainAnalysisCache.InvalidateAnalysis(chunkCoord);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error applying terrain modification: {e.Message}");
                // On error, queue for later processing
                QueueVoxelUpdate(chunkCoord, voxelPos, isAdding, true);
            }
        }
        else
        {
            // Check if this chunk is marked as solid
            if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis.IsSolid)
            {
                // Mark this solid chunk for modification
                MarkSolidChunkForModification(chunkCoord);
            }
            
            // Queue this modification for when the chunk loads
            QueueVoxelUpdate(chunkCoord, voxelPos, isAdding, true);
            
            // Request chunk load
            RequestChunkLoad(chunkCoord);
        }
    }

    public IEnumerable<Vector3Int> GetActiveChunks()
    {
        return chunks.Keys;
    }
    #endregion
    
    public void PrepareForShutdown()
    {
        Debug.Log("Preparing world for shutdown...");
        
        // Complete all pending operations
        if (operationsQueue != null)
        {
            operationsQueue.ProcessOperations();
        }

        // Force save any modified chunks
        foreach (var chunk in chunks.Values)
        {
            if (chunk != null && chunk.GetChunkData() != null)
            {
                chunk.CompleteAllJobs();
                chunk.GetChunkData().SaveData();
            }
        }

        // Clear all pending updates and queues
        lock (updateLock)
        {
            pendingVoxelUpdates.Clear();
            pendingDensityPointUpdates.Clear();
            chunksWithPendingNeighborUpdates.Clear();
        }

        // Save terrain analysis cache
        TerrainAnalysisCache.OnApplicationQuit();

        Debug.Log("World shutdown preparation complete");
    }
    public void OnApplicationQuit(){
        TerrainAnalysisCache.OnApplicationQuit();
    }
}