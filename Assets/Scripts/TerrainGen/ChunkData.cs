// REPLACE ENTIRE FILE
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;
using static Chunk;
using NelsUtils;
using System;

[System.Serializable]
public class ChunkData : System.IDisposable
{
    // Serialized fields for save/load
    [SerializeField] private Vector3Int chunkCoordinate;
    [SerializeField] private ChunkConfigurations.ChunkStatus status;
    [SerializeField] public float[] serializedDensityValues;
    [SerializeField] public int[] serializedVoxelStates;
    [SerializeField] public float[] serializedVoxelHitpoints;
    
    // Runtime configuration
    private readonly int chunkSize;
    private readonly float voxelSize;
    private readonly int totalPointsPerAxis;
    private readonly float surfaceLevel;

    // Runtime state
    private bool hasModifiedData;
    private bool isDisposed;
    private float lastAccessTime;
    private int poolId;

    // Native collections for runtime
    private NativeArray<DensityPoint> densityPoints;
    private NativeArray<Voxel> voxelData;


    // Properties
    public Vector3Int ChunkCoordinate => chunkCoordinate;
    public ChunkConfigurations.ChunkStatus Status => status;
    public bool HasModifiedData 
    { 
        get => hasModifiedData; 
        set => hasModifiedData = value;
    }
    public bool IsDisposed => isDisposed;
    public float LastAccessTime => lastAccessTime;
    public int PoolId => poolId;
    public NativeArray<DensityPoint> DensityPoints => densityPoints;
    public NativeArray<Voxel> VoxelData => voxelData;
    public int TotalPointsPerAxis => totalPointsPerAxis;
    public int ChunkSize => chunkSize;
    public bool IsCreated => densityPoints.IsCreated && voxelData.IsCreated;
    public float SurfaceLevel => surfaceLevel;
    public float VoxelSize => voxelSize;

    public bool HasSavedData { get; private set; }

    private bool isEmptyChunk;
    private bool isSolidChunk;
    public bool IsEmptyChunk => isEmptyChunk;
    public bool IsSolidChunk => isSolidChunk;

    private float[] pooledDensityValues;
    private int[]   pooledVoxelStates;
    private float[] pooledVoxelHitpoints;

    private static bool QuickCheckLoggingEnabled =>
        World.Instance != null &&
        World.Instance.Config != null &&
        World.Instance.Config.enableQuickCheckLogs;

    private static bool ChunkIoLoggingEnabled =>
        World.Instance != null &&
        World.Instance.Config != null &&
        World.Instance.Config.enableChunkIOLogs;

    private static void LogQuickCheck(string message)
    {
        if (QuickCheckLoggingEnabled)
        {
            Debug.Log(message);
        }
    }

    private static void LogChunkIo(string message)
    {
        if (ChunkIoLoggingEnabled)
        {
            Debug.Log(message);
        }
    }

    // The constructor now just sets up basic fields, no big allocations:
    public ChunkData(Vector3Int coordinate, int chunkSize, float surfaceLevel, float voxelSize, int poolId)
    {
        this.chunkCoordinate = coordinate;
        this.chunkSize       = chunkSize;
        this.surfaceLevel    = surfaceLevel;
        this.voxelSize       = voxelSize;
        this.poolId          = poolId;
        this.totalPointsPerAxis = chunkSize + 1;
        this.status = ChunkConfigurations.ChunkStatus.None;
        
        // No calls to InitializeArrays() or ClearArrays() here!
        // We'll do that later if we truly need them.
        UpdateAccessTime();

        // Optionally: Attempt to load from disk now, but only if you know the chunk definitely has data.
        // If you do that, the load method can call EnsureArraysCreated() just before copying data.
    }

    /// <summary>
    /// Actually allocate (or pull from pool) the arrays if they aren't created yet.
    /// Call this right before you do density generation or load them from file.
    /// </summary>
    public void EnsureArraysCreated()
    {
        if (isDisposed)
        {
            throw new ObjectDisposedException($"Cannot initialize arrays for chunk {ChunkCoordinate} - already disposed");
        }
        
        // Native Arrays - These MUST be created for jobs
        if (!densityPoints.IsCreated)
        {
            int totalPoints = totalPointsPerAxis * totalPointsPerAxis * totalPointsPerAxis;
            densityPoints = new NativeArray<DensityPoint>(totalPoints, Allocator.Persistent);
        }

        if (!voxelData.IsCreated)
        {
            int totalVoxels = chunkSize * chunkSize * chunkSize;
            voxelData = new NativeArray<Voxel>(totalVoxels, Allocator.Persistent);
        }

        // For pooled arrays, create them once and cache them, don't recreate them
        if (pooledDensityValues == null)
        {
            int totalPoints = totalPointsPerAxis * totalPointsPerAxis * totalPointsPerAxis;
            // Create the array directly instead of using the pool
            pooledDensityValues = new float[totalPoints];
        }
        
        if (pooledVoxelStates == null)
        {
            int totalVoxels = chunkSize * chunkSize * chunkSize;
            // Create the array directly instead of using the pool
            pooledVoxelStates = new int[totalVoxels];
        }
        
        if (pooledVoxelHitpoints == null)
        {
            int totalVoxels = chunkSize * chunkSize * chunkSize;
            // Create the array directly instead of using the pool
            pooledVoxelHitpoints = new float[totalVoxels];
        }
    }


     /// <summary>
    /// If needed, clear all arrays to default. 
    /// We only do this if we *know* we want a fresh chunk with no data.
    /// </summary>
    public void ClearArrays()
    {
        // Ensure they're allocated first
        EnsureArraysCreated();

        // Clear native arrays
        for (int i = 0; i < densityPoints.Length; i++)
        {
            densityPoints[i] = new DensityPoint();
        }
        for (int i = 0; i < voxelData.Length; i++)
        {
            voxelData[i] = new Voxel();
        }

        // Clear the pooled standard arrays
        Array.Clear(pooledDensityValues, 0, pooledDensityValues.Length);
        Array.Clear(pooledVoxelStates, 0, pooledVoxelStates.Length);
        Array.Clear(pooledVoxelHitpoints, 0, pooledVoxelHitpoints.Length);
    }

    public bool QuickTerrainCheck(Vector3Int coord)
    {
        LogQuickCheck($"[QuickCheck] Running for chunk {coord}");
        
        // Log chunk trace if enabled
        if (World.Instance != null && World.Instance.IsChunkTraced(coord))
        {
            Debug.Log($"[CHUNK_TRACE:{coord}] QuickTerrainCheck starting");
        }
        
        // Zero: Skip quick check if chunk is already known to be modified
        if (HasModifiedData)
        {
            LogQuickCheck($"[QuickCheck] Chunk {coord} has local modifications, no early exit");
            if (World.Instance != null && World.Instance.IsChunkTraced(coord))
            {
                Debug.Log($"[CHUNK_TRACE:{coord}] QuickTerrainCheck: HasModifiedData=true, returning false");
            }
            return false;
        }

        // CRITICAL FIX: Check for pending updates and marked for modification BEFORE using cached data
        // This ensures chunks that need modifications don't skip full loading even if they're queued before mining operations
        if (World.Instance.HasPendingUpdates(coord))
        {
            LogQuickCheck($"[QuickCheck] Chunk {coord} has pending updates, no early exit");
            if (World.Instance != null && World.Instance.IsChunkTraced(coord))
            {
                Debug.Log($"[CHUNK_TRACE:{coord}] QuickTerrainCheck: HasPendingUpdates=true, returning false");
            }
            return false;
        }

        // Skip if marked for modification
        if (World.Instance.IsSolidChunkMarkedForModification(coord))
        {
            LogQuickCheck($"[QuickCheck] Solid chunk {coord} marked for modification, no early exit");
            if (World.Instance != null && World.Instance.IsChunkTraced(coord))
            {
                Debug.Log($"[CHUNK_TRACE:{coord}] QuickTerrainCheck: IsSolidChunkMarkedForModification=true, returning false");
            }
            return false;
        }
        
        // CRITICAL FIX: Never skip chunks that were loaded from saved data
        // The sparse sampling (every 2nd point) can miss boundary modifications from adjacent chunk mining
        // If a chunk has saved data, it was modified at some point - always regenerate mesh to ensure accuracy
        if (HasSavedData)
        {
            LogQuickCheck($"[QuickCheck] Chunk {coord} has saved data, no early exit (prevents missing boundary modifications)");
            if (World.Instance != null && World.Instance.IsChunkTraced(coord))
            {
                Debug.Log($"[CHUNK_TRACE:{coord}] QuickTerrainCheck: HasSavedData=true, returning false");
            }
            return false;
        }

        // First: Check terrain analysis cache
        bool cacheHasValidData = false;
        if (TerrainAnalysisCache.TryGetAnalysis(coord, out var analysisData))
        {
            // If this chunk was modified, we should always process it fully
            if (analysisData.WasModified)
            {
                LogQuickCheck($"[QuickCheck] Chunk {coord} was previously modified, no early exit");
                return false;
            }
            
            // If analysis says it's empty or solid, we can trust the cache
            if (analysisData.IsEmpty || analysisData.IsSolid)
            {
                cacheHasValidData = true;
                isEmptyChunk = analysisData.IsEmpty;
                isSolidChunk = analysisData.IsSolid;
                LogQuickCheck($"[QuickCheck] Using cached analysis for chunk {coord}: Empty={isEmptyChunk}, Solid={isSolidChunk}");
            }
        }

        // If we have valid cache data OR we need to sample the densities
        if (cacheHasValidData || densityPoints.IsCreated)
        {

            // If we don't have cache data, we need to sample densities
            if (!cacheHasValidData && densityPoints.IsCreated)
            {
                // Sample densities to determine if empty or solid
                bool hasUnderSurface = false;
                bool hasOverSurface = false;
                int sampleSpacing = 2; // More samples for accuracy

                for (int x = 0; x < totalPointsPerAxis; x += sampleSpacing)
                for (int y = 0; y < totalPointsPerAxis; y += sampleSpacing)
                for (int z = 0; z < totalPointsPerAxis; z += sampleSpacing)
                {
                    int idx = x + totalPointsPerAxis * (y + totalPointsPerAxis * z);
                    if (idx >= densityPoints.Length) continue;

                    float density = densityPoints[idx].density;
                    if (density < surfaceLevel) hasUnderSurface = true;
                    if (density > surfaceLevel) hasOverSurface = true;

                    // Mixed terrain - cannot skip
                    if (hasUnderSurface && hasOverSurface)
                    {
                        LogQuickCheck($"[QuickCheck] Chunk {coord} has mixed terrain, no early exit");
                        TerrainAnalysisCache.SaveAnalysis(coord, false, false, false);
                        return false;
                    }
                }

                // Set chunk type based on sampling
                isEmptyChunk = !hasUnderSurface;
                isSolidChunk = !hasOverSurface;
                
                // CRITICAL FIX: Don't save analysis if this chunk or its neighbors have pending updates
                // This prevents race conditions where cache is saved before density modifications are applied
                bool hasPendingUpdates = World.Instance.HasPendingUpdates(coord);
                bool hasNeighborUpdates = CheckNeighborsForUpdates(coord);
                
                if (hasPendingUpdates || hasNeighborUpdates)
                {
                    LogQuickCheck($"[QuickCheck] NOT saving analysis for chunk {coord} - hasPendingUpdates: {hasPendingUpdates}, neighborHasUpdates: {hasNeighborUpdates}");
                }
                else
                {
                // Save analysis for future reference
                TerrainAnalysisCache.SaveAnalysis(coord, isEmptyChunk, isSolidChunk, false);
                LogQuickCheck($"[QuickCheck] Sampled chunk {coord}: Empty={isEmptyChunk}, Solid={isSolidChunk}");
                }
            }
            
            // By this point we have determined if it's empty/solid either from cache or sampling
            // Check if any neighbors have pending modifications
            bool neighborHasUpdates = CheckNeighborsForUpdates(coord);
            if (neighborHasUpdates)
            {
                LogQuickCheck($"[QuickCheck] Chunk {coord} has neighbor with updates, no early exit");
                return false;
            }
            
            // We can skip full loading!
            LogQuickCheck($"[QuickCheck] SUCCESS: Chunk {coord} can skip full loading - Empty:{isEmptyChunk}, Solid:{isSolidChunk}");
            return true;
        }
        
        // If density points aren't created, we can't do quick check
        LogQuickCheck($"[QuickCheck] Chunk {coord} doesn't have density data yet, no early exit");
        return false;
    }

    private bool CheckNeighborsForUpdates(Vector3Int coord)
    {
        // Check immediate neighbors for pending updates
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dz == 0) continue; // Skip center
            
            Vector3Int neighborCoord = coord + new Vector3Int(dx, 0, dz);
            
            // Check for pending updates in neighbor
            if (World.Instance.HasPendingUpdates(neighborCoord))
            {
                return true;
            }
            
            // Check if neighbor is marked for modification
            if (World.Instance.IsSolidChunkMarkedForModification(neighborCoord))
            {
                return true;
            }
        }
        
        return false;
    }

    public bool TryLoadData()
    {
        if (SaveSystem.LoadChunkData(ChunkCoordinate, this))
        {
            LogChunkIo($"[ChunkData] TryLoadData succeeded for {ChunkCoordinate}");
            EnsureArraysCreated(); // Ensure arrays are created before loading
            
            // CRITICAL FIX: Only call LoadFromSerialization if serialized fields exist
            // For binary format, data is already loaded directly into NativeArrays by BinaryChunkSerializer
            // For JSON format, SaveSystem.LoadChunkData already calls LoadFromSerialization
            // This check prevents "Cannot load from null serialized data" errors for binary-saved chunks
            if (serializedDensityValues != null && serializedVoxelStates != null && serializedVoxelHitpoints != null)
            {
                LoadFromSerialization();
            }
            
            ValidateDensityPoints(); // Validate loaded data
            
            // CRITICAL FIX: Check if this was a modified solid chunk and update terrain analysis
            if (HasModifiedData)
            {
                // Check if this was a solid chunk by sampling a few density points
                bool hasUnderSurface = false;
                bool hasOverSurface = false;
                bool wasPotentiallySolid = false;
                
                // Only do this check if we have density points
                if (densityPoints.IsCreated)
                {
                    // Quick random sampling to check if this might have been a solid chunk
                    int samplesPerDimension = 3;
                    int step = totalPointsPerAxis / (samplesPerDimension + 1);
                    
                    for (int x = step; x < totalPointsPerAxis && !hasUnderSurface && !hasOverSurface; x += step)
                    for (int y = step; y < totalPointsPerAxis && !hasUnderSurface && !hasOverSurface; y += step)
                    for (int z = step; z < totalPointsPerAxis && !hasUnderSurface && !hasOverSurface; z += step)
                    {
                        int index = Coord.GetDensityPointIndex(new Vector3Int(x, y, z), totalPointsPerAxis);
                        if (index < densityPoints.Length)
                        {
                            float density = densityPoints[index].density;
                            if (density < surfaceLevel) hasUnderSurface = true;
                            if (density > surfaceLevel) hasOverSurface = true;
                        }
                    }
                    
                    wasPotentiallySolid = !hasOverSurface && !hasUnderSurface;
                }
                
                // If this was a potentially solid chunk or terrain analysis says it's solid,
                // make sure to invalidate the cache and mark it appropriately
                if (wasPotentiallySolid || 
                    TerrainAnalysisCache.TryGetAnalysis(ChunkCoordinate, out var analysis) && 
                    (analysis.IsSolid || analysis.IsEmpty))
                {
                    // Invalidate the cache entry for this chunk
                    TerrainAnalysisCache.InvalidateAnalysis(ChunkCoordinate);
                    
                    // Save a new analysis with the correct state
                    TerrainAnalysisCache.SaveAnalysis(
                        ChunkCoordinate, 
                        hasUnderSurface == false && hasOverSurface == true,  // isEmpty
                        hasUnderSurface == false && hasOverSurface == false, // isSolid
                        true  // wasModified - critical flag!
                    );
                    
                    // Also mark it for modification in World
                    if (World.Instance != null)
                    {
                        World.Instance.MarkSolidChunkForModification(ChunkCoordinate);
                    }
                }
            }
            
            HasSavedData = true;
            return true;
        }
        else
        {
            LogChunkIo($"[ChunkData] TryLoadData found no saved data for {ChunkCoordinate}");
        }
        HasSavedData = false;
        return false;
    }

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

        // Make sure our arrays are created before we access them
        EnsureArraysCreated();

        if (!densityPoints.IsCreated || !voxelData.IsCreated)
        {
            Debug.LogError($"Native arrays not created for chunk {chunkCoordinate}");
            return;
        }

        int totalPoints = totalPointsPerAxis * totalPointsPerAxis * totalPointsPerAxis;
        int totalVoxels = chunkSize * chunkSize * chunkSize;
        
        // Validate array lengths
        if (serializedDensityValues.Length != totalPoints)
        {
            Debug.LogError($"Density data size mismatch for chunk {chunkCoordinate}: Expected {totalPoints}, got {serializedDensityValues.Length}");
            return;
        }
        
        if (serializedVoxelStates.Length != totalVoxels || serializedVoxelHitpoints.Length != totalVoxels)
        {
            Debug.LogError($"Voxel data size mismatch for chunk {chunkCoordinate}");
            return;
        }
        
        bool hasModifications = false;
        
        // Copy from serialized arrays to native arrays
        for (int i = 0; i < totalPoints; i++)
        {
            int x = i % totalPointsPerAxis;
            int y = (i / totalPointsPerAxis) % totalPointsPerAxis;
            int z = i / (totalPointsPerAxis * totalPointsPerAxis);

            float3 pos = new float3(x, y, z);
            float density = serializedDensityValues[i];
            densityPoints[i] = new DensityPoint(pos, density);
            
            // Check if this density differs from what would be generated
            if (density != 0) // You might want a more sophisticated check here
            {
                hasModifications = true;
            }
        }

        for (int i = 0; i < totalVoxels; i++)
        {
            voxelData[i] = new Voxel(serializedVoxelStates[i], serializedVoxelHitpoints[i]);
            if (serializedVoxelStates[i] != 0 || serializedVoxelHitpoints[i] != 0)
            {
                hasModifications = true;
            }
        }

        hasModifiedData = hasModifications;
        HasSavedData = true;
    }

    public void ValidateDensityPoints()
    {
        if (!densityPoints.IsCreated)
            return;
            
        bool foundInvalid = false;
        int totalPoints = totalPointsPerAxis * totalPointsPerAxis * totalPointsPerAxis;
        
        for (int i = 0; i < totalPoints; i++)
        {
            var point = densityPoints[i];
            
            // Check for NaN or Infinity
            if (float.IsNaN(point.density) || float.IsInfinity(point.density))
            {
                point.density = 1.0f;  // Default to outside surface
                densityPoints[i] = point;
                foundInvalid = true;
            }
            
            // Check for invalid position
            bool invalidPosition = false;
            float3 pos = point.position;
            
            invalidPosition = float.IsNaN(pos.x) || float.IsInfinity(pos.x) ||
                            float.IsNaN(pos.y) || float.IsInfinity(pos.y) ||
                            float.IsNaN(pos.z) || float.IsInfinity(pos.z);
                            
            if (invalidPosition)
            {
                // Reconstruct position from index
                int x = i % totalPointsPerAxis;
                int y = (i / totalPointsPerAxis) % totalPointsPerAxis;
                int z = i / (totalPointsPerAxis * totalPointsPerAxis);
                
                point.position = new float3(x, y, z);
                densityPoints[i] = point;
                foundInvalid = true;
            }
        }
        
        if (foundInvalid)
        {
            Debug.LogWarning($"Fixed invalid density points in chunk {chunkCoordinate}");
            hasModifiedData = true;
        }
    }

    public void PrepareForSerialization()
    {
        if (!densityPoints.IsCreated || !voxelData.IsCreated)
        {
            Debug.LogError($"Cannot prepare serialization for chunk {ChunkCoordinate} - Native arrays not created");
            return;
        }

        // Ensure our pooled arrays exist
        EnsureArraysCreated();

        // Force completion of any pending jobs before copying data
        if (World.Instance.TryGetChunk(ChunkCoordinate, out Chunk chunk))
        {
            chunk.CompleteAllJobs();
        }

        // Copy from native arrays into the pooled arrays for saving
        for (int i = 0; i < densityPoints.Length; i++)
        {
            pooledDensityValues[i] = densityPoints[i].density;
        }

        for (int i = 0; i < voxelData.Length; i++)
        {
            pooledVoxelStates[i] = voxelData[i].isActive;
            pooledVoxelHitpoints[i] = voxelData[i].hitpoints;
        }

        // Set the serialized fields to point to our pooled arrays
        serializedDensityValues = pooledDensityValues;
        serializedVoxelStates = pooledVoxelStates;
        serializedVoxelHitpoints = pooledVoxelHitpoints;
    }

    public void Reset()
    {
        // If we truly want to reset the chunk to "empty" or "none" state:
        ClearArrays();
        status = ChunkConfigurations.ChunkStatus.None;
        hasModifiedData = false;
        UpdateAccessTime();
    }

    public void Dispose()
    {
        if (!isDisposed)
        {
            if (HasModifiedData)
            {
                SaveData();
            }
            if (densityPoints.IsCreated)  densityPoints.Dispose();
            if (voxelData.IsCreated)      voxelData.Dispose();

            isDisposed = true;
        }
    }



    // Data access methods
    public void SetDensityPoint(int index, DensityPoint value)
    {
        ValidateState();
        ValidateIndex(index, densityPoints.Length);

        densityPoints[index] = value;
        hasModifiedData = true;  // Just mark as modified, no save
        UpdateAccessTime();
    }

    public void SetVoxel(int index, Voxel value)
    {
        ValidateState();
        ValidateIndex(index, voxelData.Length);

        voxelData[index] = value;
        hasModifiedData = true;  // Just mark as modified, no save
        UpdateAccessTime();
    }

    /// <summary>
    /// Applies a modification from the modification log directly to the chunk data
    /// Used when loading chunks with pending modifications
    /// </summary>
    public void ApplyModificationFromLog(Vector3Int voxelPos, bool isAdding, float densityChange)
    {
        // Ensure arrays are created
        EnsureArraysCreated();
        
        // Validate voxel position
        if (!Coord.IsVoxelPositionValid(voxelPos, chunkSize))
        {
            Debug.LogWarning($"[ChunkData] Invalid voxel position {voxelPos} for chunk {chunkCoordinate}");
            return;
        }
        
        if (isAdding)
        {
            // Adding a voxel - set it to active state
            int voxelIndex = Coord.GetVoxelIndex(voxelPos, chunkSize);
            if (voxelIndex >= 0 && voxelIndex < voxelData.Length)
            {
                float defaultHealth = 1.0f; // Default voxel health
                voxelData[voxelIndex] = new Voxel(1, defaultHealth); // 1 = VOXEL_ACTIVE
                hasModifiedData = true;
                
                LogChunkIo($"[ChunkData] Applied ADD modification at {voxelPos} in chunk {chunkCoordinate}");
            }
        }
        else
        {
            // Removing a voxel - apply density change
            if (densityChange != 0)
            {
                // Apply density change to surrounding density points
                // A voxel is defined by 8 corner density points
                for (int dx = 0; dx <= 1; dx++)
                for (int dy = 0; dy <= 1; dy++)
                for (int dz = 0; dz <= 1; dz++)
                {
                    Vector3Int densityPos = voxelPos + new Vector3Int(dx, dy, dz);
                    
                    if (Coord.IsDensityPositionValid(densityPos, totalPointsPerAxis))
                    {
                        int densityIndex = Coord.GetDensityPointIndex(densityPos, totalPointsPerAxis);
                        if (densityIndex >= 0 && densityIndex < densityPoints.Length)
                        {
                            var currentDensity = densityPoints[densityIndex];
                            float newDensity = currentDensity.density + densityChange;
                            densityPoints[densityIndex] = new DensityPoint(currentDensity.position, newDensity);
                            hasModifiedData = true;
                        }
                    }
                }
                
                // Update voxel state based on new density
                int voxelIndex = Coord.GetVoxelIndex(voxelPos, chunkSize);
                if (voxelIndex >= 0 && voxelIndex < voxelData.Length)
                {
                    // Check if voxel should be active or inactive based on surrounding density
                    bool shouldBeActive = false;
                    for (int dx = 0; dx <= 1 && !shouldBeActive; dx++)
                    for (int dy = 0; dy <= 1 && !shouldBeActive; dy++)
                    for (int dz = 0; dz <= 1 && !shouldBeActive; dz++)
                    {
                        Vector3Int densityPos = voxelPos + new Vector3Int(dx, dy, dz);
                        if (Coord.IsDensityPositionValid(densityPos, totalPointsPerAxis))
                        {
                            int densityIndex = Coord.GetDensityPointIndex(densityPos, totalPointsPerAxis);
                            if (densityIndex >= 0 && densityIndex < densityPoints.Length)
                            {
                                if (densityPoints[densityIndex].density < surfaceLevel)
                                {
                                    shouldBeActive = true;
                                    break;
                                }
                            }
                        }
                    }
                    
                    if (shouldBeActive)
                    {
                        var currentVoxel = voxelData[voxelIndex];
                        voxelData[voxelIndex] = new Voxel(1, currentVoxel.hitpoints); // Keep active
                    }
                    else
                    {
                        voxelData[voxelIndex] = new Voxel(0, 0); // Make inactive
                    }
                }
                
                LogChunkIo($"[ChunkData] Applied REMOVE modification at {voxelPos} with density change {densityChange} in chunk {chunkCoordinate}");
            }
            else
            {
                // Simple removal without density change
                int voxelIndex = Coord.GetVoxelIndex(voxelPos, chunkSize);
                if (voxelIndex >= 0 && voxelIndex < voxelData.Length)
                {
                    voxelData[voxelIndex] = new Voxel(0, 0); // 0 = VOXEL_INACTIVE
                    hasModifiedData = true;
                    
                    LogChunkIo($"[ChunkData] Applied REMOVE modification at {voxelPos} in chunk {chunkCoordinate}");
                }
            }
        }
        
        UpdateAccessTime();
    }

    public void UpdateAccessTime()
    {
        lastAccessTime = Time.time;
    }

    // Validation
    public void ValidateState()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException($"ChunkData for chunk {chunkCoordinate} has been disposed");
        }

        if (!IsCreated)
        {
            throw new InvalidOperationException($"ChunkData for chunk {chunkCoordinate} not properly initialized");
        }
    }

    private void ValidateIndex(int index, int length)
    {
        if (index < 0 || index >= length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), 
                $"Index {index} out of range for length {length} in chunk {chunkCoordinate}");
        }
    }

    // State management
    public void SetStatus(ChunkConfigurations.ChunkStatus newStatus)
    {
        status = newStatus;
    }

    // Save data
    public void SaveData()
    {
        if (HasModifiedData)
        {
            Debug.Log($"[ChunkData] SaveData invoked for {ChunkCoordinate} (modified={HasModifiedData})");
            // Ensure arrays are created before preparing for serialization
            EnsureArraysCreated();
            
            try 
            {
                // Get the chunk and ensure all jobs are complete
                if (World.Instance.TryGetChunk(ChunkCoordinate, out Chunk chunk))
                {
                    // Complete ALL jobs before attempting to save
                    chunk.CompleteAllJobs();
                }

                PrepareForSerialization();
                SaveSystem.SaveChunkData(this);
                hasModifiedData = false;
                
                // Update the HasSavedData flag
                HasSavedData = true;
                
                // CRITICAL FIX: If this was a solid or empty chunk before, make sure to update the terrain analysis
                if (TerrainAnalysisCache.TryGetAnalysis(ChunkCoordinate, out var analysis) && 
                    (analysis.IsSolid || analysis.IsEmpty))
                {
                    // We need to reevaluate if this chunk is still solid/empty after modifications
                    // Do a more thorough check to see if it's still solid or empty
                    
                    bool hasUnderSurface = false;
                    bool hasOverSurface = false;
                    int sampleSpacing = 4;
                    
                    for (int x = 0; x < totalPointsPerAxis; x += sampleSpacing)
                    for (int y = 0; y < totalPointsPerAxis; y += sampleSpacing)
                    for (int z = 0; z < totalPointsPerAxis; z += sampleSpacing)
                    {
                        int idx = x + totalPointsPerAxis * (y + totalPointsPerAxis * z);
                        if (idx >= densityPoints.Length) continue;

                        float density = densityPoints[idx].density;
                        if (density < surfaceLevel) hasUnderSurface = true;
                        if (density > surfaceLevel) hasOverSurface = true;

                        if (hasUnderSurface && hasOverSurface) break;
                    }
                    
                    bool stillEmpty = !hasUnderSurface;
                    bool stillSolid = !hasOverSurface;
                    
                    // If the chunk has changed from solid/empty to mixed, update the terrain analysis
                    if ((analysis.IsSolid && !stillSolid) || (analysis.IsEmpty && !stillEmpty))
                    {
                        TerrainAnalysisCache.InvalidateAnalysis(ChunkCoordinate);
                        
                        // Create a new analysis entry with the correct state and explicitly mark as modified
                        TerrainAnalysisCache.SaveAnalysis(ChunkCoordinate, stillEmpty, stillSolid, true);
                        
                        Debug.Log($"Updated terrain analysis for modified chunk {ChunkCoordinate}: " + 
                                $"Was: {(analysis.IsSolid ? "Solid" : analysis.IsEmpty ? "Empty" : "Mixed")}, " +
                                $"Now: {(stillSolid ? "Solid" : stillEmpty ? "Empty" : "Mixed")}, " +
                                $"Marked as modified: true");
                                
                        // CRITICAL FIX: Also tell World that this solid chunk was modified
                        if (analysis.IsSolid && !stillSolid && World.Instance != null)
                        {
                            World.Instance.MarkSolidChunkForModification(ChunkCoordinate);
                        }
                    }
                }
                
                // Update last access time
                UpdateAccessTime();
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save chunk data for {ChunkCoordinate}: {e.Message}\n{e.StackTrace}");
                World.Instance.QuarantineChunk(ChunkCoordinate, 
                    $"Save failed: {e.Message}", 
                    ChunkConfigurations.ChunkStatus.Modified);
            }
        }
        else
        {
            Debug.Log($"Skipping save for unmodified chunk {ChunkCoordinate}");
        }
    }
}