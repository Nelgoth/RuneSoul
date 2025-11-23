using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using NelsUtils;

/// <summary>
/// Batches terrain modifications together for efficient processing.
/// Dramatically reduces per-voxel overhead by processing modifications in groups.
/// </summary>
public class TerrainModificationBatch : IDisposable
{
    // Modification data structure
    public struct VoxelModification
    {
        public Vector3Int chunkCoord;
        public Vector3Int voxelPos;
        public Vector3 worldPos;
        public bool isAdding;
        public bool propagate;
        public float timestamp;

        public VoxelModification(Vector3Int chunk, Vector3Int voxel, Vector3 world, bool adding, bool prop)
        {
            chunkCoord = chunk;
            voxelPos = voxel;
            worldPos = world;
            isAdding = adding;
            propagate = prop;
            timestamp = Time.time;
        }
    }

    // Pooled collections
    private List<VoxelModification> modifications;
    private HashSet<Vector3Int> affectedChunks;
    private Dictionary<Vector3Int, List<Vector3>> densityUpdatesByChunk;
    
    // Batch configuration
    private float batchAccumulationTime;
    private int maxBatchSize;
    private int neighborChunkRadius;
    
    // Timing
    private float lastFlushTime;
    private bool isProcessing;
    
    // World reference
    private World world;
    
    // Configuration
    private float voxelSize;
    private int chunkSize;
    private float densityInfluenceRadius;

    public TerrainModificationBatch(World worldInstance, TerrainConfigs config)
    {
        world = worldInstance;
        
        // Get configuration
        batchAccumulationTime = config.batchAccumulationTime;
        maxBatchSize = config.maxBatchSize;
        neighborChunkRadius = config.neighborChunkRadius;
        voxelSize = config.voxelSize;
        chunkSize = config.chunkSize;
        densityInfluenceRadius = config.densityInfluenceRadius;
        
        // Initialize pooled collections
        modifications = ModificationDataPool.GetModificationList();
        affectedChunks = ModificationDataPool.GetChunkSet();
        densityUpdatesByChunk = ModificationDataPool.GetDensityUpdateDict();
        
        // Set lastFlushTime to current time to prevent immediate flushing
        // This gives world time to fully initialize before first batch flush
        lastFlushTime = Time.time;
        isProcessing = false;
        
        Debug.Log($"[TerrainBatch] Initialized - will delay first flush by {batchAccumulationTime}s");
    }

    /// <summary>
    /// Add a voxel modification to the batch
    /// </summary>
    public void AddModification(Vector3Int chunkCoord, Vector3Int voxelPos, Vector3 worldPos, bool isAdding, bool propagate)
    {
        if (isProcessing)
        {
            Debug.LogWarning("[TerrainBatch] Cannot add modification while processing batch");
            return;
        }
        
        // Safety check for null world reference
        if (world == null)
        {
            Debug.LogError("[TerrainBatch] World reference is null, cannot add modification");
            return;
        }

        modifications.Add(new VoxelModification(chunkCoord, voxelPos, worldPos, isAdding, propagate));
        
        // Auto-flush if batch is full
        if (modifications.Count >= maxBatchSize)
        {
            FlushBatch();
        }
    }

    /// <summary>
    /// Check if batch should be flushed based on time or size
    /// </summary>
    public bool ShouldFlush()
    {
        // Don't flush empty batch
        if (modifications.Count == 0) return false;
        
        // Don't flush if world reference is gone
        if (world == null) return false;
        
        float timeSinceLastFlush = Time.time - lastFlushTime;
        return timeSinceLastFlush >= batchAccumulationTime || modifications.Count >= maxBatchSize;
    }

    /// <summary>
    /// Process all accumulated modifications in a single batch
    /// </summary>
    public void FlushBatch()
    {
        if (modifications.Count == 0 || isProcessing) return;
        
        // Safety check for world reference
        if (world == null)
        {
            Debug.LogError("[TerrainBatch] World reference is null during FlushBatch, clearing modifications");
            modifications.Clear();
            return;
        }
        
        isProcessing = true;
        
        try
        {
            // Step 1: Calculate all affected chunks in a single pass
            CalculateAffectedChunksForBatch();
            
            // Step 2: Group density updates by chunk
            GroupDensityUpdatesByChunk();
            
            // Step 3: Apply modifications to chunks
            ApplyModificationsToChunks();
            
            // Step 4: Process density updates in bulk
            ProcessBulkDensityUpdates();
            
            // Step 5: Regenerate meshes for affected chunks
            RegenerateMeshesForAffectedChunks();
            
            // Clear batch
            modifications.Clear();
            affectedChunks.Clear();
            densityUpdatesByChunk.Clear();
            
            lastFlushTime = Time.time;
        }
        catch (Exception e)
        {
            Debug.LogError($"[TerrainBatch] Error flushing batch: {e.Message}\n{e.StackTrace}");
        }
        finally
        {
            isProcessing = false;
        }
    }

    /// <summary>
    /// Calculate all affected chunks for the entire batch in one pass
    /// </summary>
    private void CalculateAffectedChunksForBatch()
    {
        affectedChunks.Clear();
        
        // Calculate expanded radius for boundary cases
        float radius = voxelSize * (densityInfluenceRadius + 1f);
        
        foreach (var mod in modifications)
        {
            // Add the chunk containing the modification
            affectedChunks.Add(mod.chunkCoord);
            
            // Add neighboring chunks within radius
            for (int dx = -neighborChunkRadius; dx <= neighborChunkRadius; dx++)
            for (int dy = -neighborChunkRadius; dy <= neighborChunkRadius; dy++)
            for (int dz = -neighborChunkRadius; dz <= neighborChunkRadius; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                
                Vector3Int neighborCoord = mod.chunkCoord + new Vector3Int(dx, dy, dz);
                
                // Check if this neighbor is actually within the influence radius
                float distance = world.DistanceToChunkBounds(mod.worldPos, neighborCoord);
                if (distance <= radius + voxelSize)
                {
                    affectedChunks.Add(neighborCoord);
                }
            }
        }
    }

    /// <summary>
    /// Group all density updates by chunk to minimize processing
    /// </summary>
    private void GroupDensityUpdatesByChunk()
    {
        densityUpdatesByChunk.Clear();
        
        foreach (var mod in modifications)
        {
            if (!mod.propagate) continue;
            
            foreach (var chunkCoord in affectedChunks)
            {
                if (!densityUpdatesByChunk.ContainsKey(chunkCoord))
                {
                    densityUpdatesByChunk[chunkCoord] = ModificationDataPool.GetWorldPosList();
                }
                
                densityUpdatesByChunk[chunkCoord].Add(mod.worldPos);
            }
        }
    }

    /// <summary>
    /// Apply voxel modifications to loaded chunks
    /// </summary>
    private void ApplyModificationsToChunks()
    {
        // Group modifications by chunk
        var modsByChunk = new Dictionary<Vector3Int, List<VoxelModification>>();
        
        foreach (var mod in modifications)
        {
            if (!modsByChunk.ContainsKey(mod.chunkCoord))
            {
                modsByChunk[mod.chunkCoord] = new List<VoxelModification>();
            }
            modsByChunk[mod.chunkCoord].Add(mod);
        }
        
        // Apply modifications to each chunk
        foreach (var kvp in modsByChunk)
        {
            Vector3Int chunkCoord = kvp.Key;
            List<VoxelModification> chunkMods = kvp.Value;
            
            if (!world.TryGetChunk(chunkCoord, out Chunk chunk))
            {
                // Chunk not loaded, queue for later
                foreach (var mod in chunkMods)
                {
                    world.QueueVoxelUpdateDirect(chunkCoord, mod.voxelPos, mod.isAdding, mod.propagate);
                }
                continue;
            }
            
            // Ensure chunk is ready
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            if (state.Status != ChunkConfigurations.ChunkStatus.Loaded && 
                state.Status != ChunkConfigurations.ChunkStatus.Modified)
            {
                continue;
            }
            
            chunk.CompleteAllJobs();
            chunk.EnsureDataInitialized();
            
            // Apply all modifications to this chunk
            foreach (var mod in chunkMods)
            {
                if (mod.isAdding)
                {
                    chunk.AddVoxel(mod.voxelPos);
                }
                else
                {
                    chunk.DamageVoxel(mod.voxelPos, 1);
                }
            }
            
            // Mark chunk as modified
            if (state.Status == ChunkConfigurations.ChunkStatus.Loaded)
            {
                ChunkStateManager.Instance.TryChangeState(
                    chunkCoord,
                    ChunkConfigurations.ChunkStatus.Modified,
                    ChunkConfigurations.ChunkStateFlags.Active
                );
            }
        }
    }

    /// <summary>
    /// Process density updates in bulk for all affected chunks
    /// </summary>
    private void ProcessBulkDensityUpdates()
    {
        foreach (var kvp in densityUpdatesByChunk)
        {
            Vector3Int chunkCoord = kvp.Key;
            List<Vector3> worldPositions = kvp.Value;
            
            try
            {
                // Invalidate terrain analysis cache
                TerrainAnalysisCache.InvalidateAnalysis(chunkCoord);
                
                // Check if chunk is solid and needs special handling
                if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis.IsSolid)
                {
                    world.MarkSolidChunkForModification(chunkCoord);
                }
                
                // Queue density updates
                foreach (var worldPos in worldPositions)
                {
                    world.QueueDensityUpdateDirect(chunkCoord, worldPos);
                }
                
                // Request chunk load if not loaded
                if (!world.TryGetChunk(chunkCoord, out _))
                {
                    world.RequestChunkLoad(chunkCoord, force: true);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TerrainBatch] Error processing density updates for chunk {chunkCoord}: {e.Message}");
            }
        }
        
        // Return lists to pool
        foreach (var list in densityUpdatesByChunk.Values)
        {
            ModificationDataPool.ReturnWorldPosList(list);
        }
    }

    /// <summary>
    /// Regenerate meshes for all affected chunks that are loaded
    /// </summary>
    private void RegenerateMeshesForAffectedChunks()
    {
        foreach (var chunkCoord in affectedChunks)
        {
            if (world.TryGetChunk(chunkCoord, out Chunk chunk))
            {
                var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                if (state.Status == ChunkConfigurations.ChunkStatus.Loaded || 
                    state.Status == ChunkConfigurations.ChunkStatus.Modified)
                {
                    chunk.Generate(log: false, fullMesh: false, quickCheck: false);
                }
            }
        }
    }

    public int GetBatchSize() => modifications.Count;
    public int GetAffectedChunkCount() => affectedChunks.Count;
    public bool IsProcessing() => isProcessing;

    public void Dispose()
    {
        if (modifications != null)
        {
            ModificationDataPool.ReturnModificationList(modifications);
            modifications = null;
        }
        
        if (affectedChunks != null)
        {
            ModificationDataPool.ReturnChunkSet(affectedChunks);
            affectedChunks = null;
        }
        
        if (densityUpdatesByChunk != null)
        {
            foreach (var list in densityUpdatesByChunk.Values)
            {
                ModificationDataPool.ReturnWorldPosList(list);
            }
            ModificationDataPool.ReturnDensityUpdateDict(densityUpdatesByChunk);
            densityUpdatesByChunk = null;
        }
    }
}

