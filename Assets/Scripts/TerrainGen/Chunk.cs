using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Unity.Jobs;
using Unity.Mathematics;
using System.Collections;
using NelsUtils;
using System;
using Unity.Netcode;

public class Chunk : MonoBehaviour
{
    // Constants for voxel states
    public const int VOXEL_INVALID = -1;
    public const int VOXEL_INACTIVE = 0;
    public const int VOXEL_ACTIVE = 1;

    [System.Serializable]
    public struct Voxel
    {
        public int isActive;  
        public float hitpoints;

        public Voxel(int isActive, float hitpoints)
        {
            this.isActive = isActive;
            this.hitpoints = hitpoints;
        }
    }

    // Core components and data
    private ChunkData chunkData;
    public ChunkData GetChunkData()
    {
        return chunkData;
    }
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    
    // Job and generation state
    public Coroutine generationCoroutine;
    private NativeStream vertexStream;
    private NativeStream triangleStream;
    private JobHandle densityHandle;
    private JobHandle marchingHandle;
    private bool densityJobScheduled;
    
    // State flags
    private bool isDisposed;
    public bool isMeshUpdateQueued;
    private bool isGenerationQueued;
    private bool hasQuit;
    private bool hasDestroyed;
    private bool densityGenerationComplete;
    private bool marchingCubesComplete;
    private bool isInitialized;
    public bool IsInitialized { get; private set; }

    private bool ChunkLifecycleLogsEnabled =>
        World.Instance != null &&
        World.Instance.Config != null &&
        World.Instance.Config.enableChunkLifecycleLogs;

    private bool QuickCheckLogsEnabled =>
        World.Instance != null &&
        World.Instance.Config != null &&
        World.Instance.Config.enableQuickCheckLogs;

    // Updates queue
    private List<PendingVoxelUpdate> pendingVoxelUpdates = new List<PendingVoxelUpdate>();
    private MarchingCubesBatchAllocator marchingCubesAllocator;

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

    public float lastAccessTime { get; private set; } = 0f;


    // State accessors
    public bool IsDensityGenerationComplete() => densityGenerationComplete;
    public bool IsMarchingCubesComplete() => marchingCubesComplete;
    
    private bool IsJobRunning => generationCoroutine != null || 
                           densityJobScheduled || 
                           (marchingCubesAllocator?.IsJobRunning ?? false);

    public void Initialize(int size, float surfaceLevel, float voxelSize, Vector3 worldPosition, bool quickCheck)
    {
        if (IsInitialized)
        {
            Debug.LogWarning($"Attempted to initialize already initialized chunk at {transform.position}");
            return;
        }

        try
        {
            // Calculate chunk coordinate
            Vector3Int chunkCoord = Coord.WorldToChunkCoord(worldPosition, size, voxelSize);
            if (ChunkLifecycleLogsEnabled)
            {
                Debug.Log($"Initializing chunk at position {worldPosition}, coord: {chunkCoord}, quickCheck: {quickCheck}");
            }
            
            // Initialize ChunkData
            chunkData = new ChunkData(
                chunkCoord,
                size,
                surfaceLevel,
                voxelSize,
                GetInstanceID()
            );

            gameObject.name = $"Chunk_{chunkData.ChunkCoordinate.x}_{chunkData.ChunkCoordinate.y}_{chunkData.ChunkCoordinate.z}";
            transform.position = worldPosition;
            
            SetupComponents();
            gameObject.layer = LayerMask.NameToLayer("Terrain");
            IsInitialized = true;
            UpdateAccessTime();
            
            // Get current state for debugging
            var currentState = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            if (ChunkLifecycleLogsEnabled)
            {
                Debug.Log($"Chunk {chunkCoord} current state before loading: {currentState.Status}");
            }
            
            // CRITICAL FIX: Don't try to change to Loading state if we're already in Loading state
            // This prevents the invalid "Loading -> Loading" transition that's causing issues
            bool needsStateChange = currentState.Status != ChunkConfigurations.ChunkStatus.Loading;
            bool transitionToLoadingSuccess = true;
            
            if (needsStateChange)
            {
                transitionToLoadingSuccess = ChunkStateManager.Instance.TryChangeState(
                    chunkData.ChunkCoordinate,
                    ChunkConfigurations.ChunkStatus.Loading,
                    ChunkConfigurations.ChunkStateFlags.Active
                );

                if (ChunkLifecycleLogsEnabled)
                {
                    Debug.Log($"Chunk {chunkCoord} transition to Loading: {transitionToLoadingSuccess}");
                }
            }
            else
            {
                if (ChunkLifecycleLogsEnabled)
                {
                    Debug.Log($"Chunk {chunkCoord} already in Loading state, skipping transition");
                }
            }
            
            if (!transitionToLoadingSuccess)
            {
                Debug.LogError($"Failed to transition chunk {chunkCoord} to Loading state from {currentState.Status}");
                World.Instance.QuarantineChunk(
                    chunkCoord,
                    $"Failed to transition to Loading state from {currentState.Status}",
                    currentState.Status
                );
                return;
            }
            
            // Try to load saved data first
            bool dataLoaded = chunkData.TryLoadData();
            if (ChunkLifecycleLogsEnabled)
            {
                Debug.Log($"Chunk {chunkCoord} data loaded: {dataLoaded}");
            }
            
            if (dataLoaded)
            {
                // Now transition to Loaded state
                bool transitionSuccess = ChunkStateManager.Instance.TryChangeState(
                    chunkData.ChunkCoordinate,
                    ChunkConfigurations.ChunkStatus.Loaded,
                    ChunkConfigurations.ChunkStateFlags.Active
                );
                
                if (ChunkLifecycleLogsEnabled)
                {
                    Debug.Log($"Chunk {chunkCoord} transition to Loaded: {transitionSuccess}");
                }

                // If the chunk has modifications, then transition to Modified state
                if (chunkData.HasModifiedData)
                {
                    bool modifiedTransitionSuccess = ChunkStateManager.Instance.TryChangeState(
                        chunkData.ChunkCoordinate,
                        ChunkConfigurations.ChunkStatus.Modified,
                        ChunkConfigurations.ChunkStateFlags.Active
                    );
                    
                    if (ChunkLifecycleLogsEnabled)
                    {
                        Debug.Log($"Chunk {chunkCoord} transition to Modified: {modifiedTransitionSuccess}");
                    }
                }

                QueueGeneration(log: false, fullmesh: false, quickCheck);
            }
            else
            {
                if (ChunkLifecycleLogsEnabled)
                {
                    Debug.Log($"Chunk {chunkCoord} - no saved data, generating new content with quickCheck: {quickCheck}");
                }
                QueueGeneration(log: false, fullmesh: true, quickCheck);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to initialize chunk at {worldPosition}: {e.Message}\n{e.StackTrace}");
            World.Instance.QuarantineChunk(
                chunkData?.ChunkCoordinate ?? Coord.WorldToChunkCoord(worldPosition, size, voxelSize),
                $"Initialization failed: {e.Message}",
                ChunkConfigurations.ChunkStatus.None
            );
            throw;
        }
    }

    private void SetupComponents()
    {
        if (meshFilter == null)
        {
            if (!TryGetComponent(out meshFilter))
            {
                meshFilter = gameObject.AddComponent<MeshFilter>();
            }
        }

        if (meshRenderer == null)
        {
            if (!TryGetComponent(out meshRenderer))
            {
                meshRenderer = gameObject.AddComponent<MeshRenderer>();
            }
        }

        if (meshCollider == null)
        {
            if (!TryGetComponent(out meshCollider))
            {
                meshCollider = gameObject.AddComponent<MeshCollider>();
            }
        }

        transform.localScale = Vector3.one;
    }

    public void UpdateAccessTime()
    {
        lastAccessTime = Time.time;
    }

    private void DisposeNativeContainers()
    {
        CompleteAllJobs();
        
        if (vertexStream.IsCreated)
        {
            vertexStream.Dispose();
            vertexStream = default;
        }
        
        if (triangleStream.IsCreated)
        {
            triangleStream.Dispose();
            triangleStream = default;
        }
    }

    public void EnsureDataInitialized()
    {
        if (chunkData != null)
        {
            chunkData.EnsureArraysCreated();
        }
        else
        {
            Debug.LogError($"Cannot initialize chunk data - null reference in chunk {transform.position}");
        }
    }

    public void QueueGeneration(bool log = false, bool fullmesh = true, bool quickCheck = true)
    {
        if (!IsInitialized)
        {
            Debug.LogError($"Attempted to generate uninitialized chunk at {transform.position}");
            return;
        }

        if (generationCoroutine != null)
        {
            isGenerationQueued = true;
            return;
        }

        isGenerationQueued = false;
        Generate(log, fullmesh, quickCheck);
    }

    public void Generate(bool log = false, bool fullMesh = true, bool quickCheck = true)
    {
        CompleteAllJobs(); 
        UpdateAccessTime();
        if (generationCoroutine == null)
        {
            bool shouldLog = log && fullMesh;
            generationCoroutine = StartCoroutine(GenerateChunkAsync(shouldLog, fullMesh, quickCheck));
        }
    }

    private IEnumerator GenerateChunkAsync(bool log = false, bool fullMesh = true, bool quickCheck = true)
    {
        // Validation checks
        if (!IsInitialized)
        {
            Debug.LogError($"Attempted to generate uninitialized chunk at {transform.position}");
            QuarantineChunk("Chunk not initialized");
            generationCoroutine = null;
            yield break;
        }

        if (chunkData == null)
        {
            Debug.LogError($"Null chunkData in chunk at {transform.position}");
            QuarantineChunk("ChunkData is null");
            generationCoroutine = null;
            yield break;
        }

        var timing = new ChunkGenerationTiming(chunkData.ChunkCoordinate);
        
        if (EnhancedBenchmarkManager.Instance != null)
        {
            EnhancedBenchmarkManager.Instance.BeginOperation(
                chunkData.ChunkCoordinate, 
                "ChunkGeneration"
            );
        }

        // Yield at the beginning to distribute frame cost
        yield return null;

        // STEP 1: Initialize allocator
        bool allocatorInitFailed = false;
        
        // Initialize this chunk's allocator if needed
        if (marchingCubesAllocator == null || !marchingCubesAllocator.IsCreated)
        {
            try
            {
                marchingCubesAllocator = new MarchingCubesBatchAllocator();
                marchingCubesAllocator.Initialize();
            }
            catch (Exception e)
            {
                allocatorInitFailed = true;
                Debug.LogError($"Failed to initialize MarchingCubesAllocator for chunk {chunkData.ChunkCoordinate}: {e.Message}");
                QuarantineChunk($"MarchingCubesAllocator init failed: {e.Message}");
            }
        }
        
        if (allocatorInitFailed)
        {
            timing.EndAllPhases();
            generationCoroutine = null;
            yield break;
        }

        // Yield to distribute work across frames
        yield return null;

        // STEP 2: Density generation
        bool densityFailed = false;
        
        if (fullMesh || !chunkData.DensityPoints.IsCreated)
        {
            bool hasExistingData = chunkData.HasSavedData;
            
            if (!hasExistingData)
            {
                try
                {
                    chunkData.EnsureArraysCreated();
                    timing.StartPhase("DensityFieldGeneration");

                    // Setup density job
                    DensityFieldGenerationJob densityJob = new DensityFieldGenerationJob
                    {
                        densityPoints = chunkData.DensityPoints,
                        gridSize = chunkData.TotalPointsPerAxis - 1,
                        maxHeight = World.Instance.maxHeight,
                        chunkWorldPosition = transform.position,
                        seed = World.Instance.noiseSeed,
                        frequency = World.Instance.frequency,
                        noiseScale = World.Instance.noiseScale,
                        noiseType = FastNoiseLite.NoiseType.OpenSimplex2,
                        voxelSize = chunkData.VoxelSize,
                    };

                    densityHandle = densityJob.Schedule(chunkData.DensityPoints.Length, 128);
                    densityJobScheduled = true;
                }
                catch (Exception e)
                {
                    densityFailed = true;
                    Debug.LogError($"Error scheduling density job for chunk {chunkData.ChunkCoordinate}: {e.Message}");
                    QuarantineChunk($"Density job setup failed: {e.Message}");
                }
                
                if (densityFailed)
                {
                    timing.EndAllPhases();
                    generationCoroutine = null;
                    yield break;
                }
                
                // Wait for density job to complete, yielding each frame to distribute load
                while (!densityHandle.IsCompleted)
                {
                    yield return null;
                }
                
                try
                {
                    densityHandle.Complete();
                    densityJobScheduled = false;
                    timing.EndPhase("DensityFieldGeneration");
                }
                catch (Exception e)
                {
                    densityFailed = true;
                    Debug.LogError($"Error completing density job for chunk {chunkData.ChunkCoordinate}: {e.Message}");
                    QuarantineChunk($"Density job completion failed: {e.Message}");
                }
                
                if (densityFailed)
                {
                    timing.EndAllPhases();
                    generationCoroutine = null;
                    yield break;
                }
                
                // Check if chunk is empty or solid
                    if (quickCheck && chunkData.QuickTerrainCheck(chunkData.ChunkCoordinate))
                    {
                        if (QuickCheckLogsEnabled)
                        {
                            Debug.Log($"[Chunk] QuickCheck early exit for chunk {chunkData.ChunkCoordinate} - Empty:{chunkData.IsEmptyChunk}, Solid:{chunkData.IsSolidChunk}");
                        }
                    densityGenerationComplete = true;
                    marchingCubesComplete = true;
                    
                    // First transition to Loaded state
                    Vector3Int chunkCoord = chunkData.ChunkCoordinate;
                    bool stateChangeSuccess = ChunkStateManager.Instance.TryChangeState(
                        chunkCoord,
                        ChunkConfigurations.ChunkStatus.Loaded,
                        ChunkConfigurations.ChunkStateFlags.Active
                    );
                    
                    // Notify World of completion
                    World.Instance.OnChunkGenerationComplete(chunkCoord);
                    
                    // CRITICAL FIX: Only save terrain analysis if there are NO pending updates
                    // This prevents saving stale cache data before density modifications are applied
                    // The analysis will be saved later after updates are processed
                    if (!World.Instance.HasPendingUpdates(chunkData.ChunkCoordinate))
                    {
                        // Save terrain analysis for future reference
                        TerrainAnalysisCache.SaveAnalysis(
                            chunkData.ChunkCoordinate,
                            chunkData.IsEmptyChunk,
                            chunkData.IsSolidChunk,
                            false // not modified
                        );
                    }
                    else
                    {
                        Debug.Log($"[Chunk] NOT saving terrain analysis for chunk {chunkData.ChunkCoordinate} - has pending updates");
                    }
                    
                    // Check if there are any pending updates before deciding to unload
                    if (!World.Instance.HasPendingUpdates(chunkData.ChunkCoordinate))
                    {
                        // Don't queue for unload right away - we'll handle this in OnGenerationComplete
                        // to ensure any pending operations have a chance to process
                    }
                    
                    // Complete the generation and invoke OnGenerationComplete
                    generationCoroutine = null;
                    timing.EndAllPhases();
                    
                    if (EnhancedBenchmarkManager.Instance != null)
                    {
                        EnhancedBenchmarkManager.Instance.EndOperation(chunkData.ChunkCoordinate);
                    }
                    
                    // Call OnGenerationComplete instead of immediately queueing for unload
                    OnGenerationComplete();
                    yield break;
                }
            }
            else
            {
                // We already have data loaded
                chunkData.EnsureArraysCreated();
            }

            densityGenerationComplete = true;
        }

        // Yield again to distribute frame cost
        yield return null;

        // STEP 3: Marching cubes
        bool marchingCubesFailed = false;
        
        timing.StartPhase("MarchingCubesSetup");
        int totalVoxels = (chunkData.TotalPointsPerAxis - 1) * (chunkData.TotalPointsPerAxis - 1) * 
                        (chunkData.TotalPointsPerAxis - 1);

        NativeArray<int> vertexCounter = default;
        NativeArray<int> triangleCounter = default;
        
        try
        {
            vertexCounter = new NativeArray<int>(1, Allocator.TempJob);
            triangleCounter = new NativeArray<int>(1, Allocator.TempJob);
            timing.EndPhase("MarchingCubesSetup");
        }
        catch (Exception e)
        {
            marchingCubesFailed = true;
            Debug.LogError($"Error creating counter arrays for chunk {chunkData.ChunkCoordinate}: {e.Message}");
            QuarantineChunk($"Counter array creation failed: {e.Message}");
        }
        
        if (marchingCubesFailed)
        {
            timing.EndAllPhases();
            generationCoroutine = null;
            yield break;
        }

        timing.StartPhase("MarchingCubesJob");
        
        try
        {
            MarchingCubesJob marchingCubesJob = new MarchingCubesJob
            {
                densityPoints = chunkData.DensityPoints,
                vertexBuffer = marchingCubesAllocator.VertexBuffer,
                triangleBuffer = marchingCubesAllocator.TriangleBuffer,
                vertexCount = vertexCounter,
                triangleCount = triangleCounter,
                voxelArray = chunkData.VoxelData,
                chunkSize = chunkData.TotalPointsPerAxis - 1,
                surfaceLevel = chunkData.SurfaceLevel
            };

            // Schedule using the allocator's method - use smaller batch size
            marchingHandle = marchingCubesAllocator.ScheduleJob(marchingCubesJob, totalVoxels, 32);
        }
        catch (Exception e)
        {
            marchingCubesFailed = true;
            Debug.LogError($"Error scheduling marching cubes job for chunk {chunkData.ChunkCoordinate}: {e.Message}");
            QuarantineChunk($"Marching cubes job setup failed: {e.Message}");
        }
        
        if (marchingCubesFailed)
        {
            if (vertexCounter.IsCreated) vertexCounter.Dispose();
            if (triangleCounter.IsCreated) triangleCounter.Dispose();
            timing.EndAllPhases();
            generationCoroutine = null;
            yield break;
        }
        
        // Yield while waiting for marching cubes to complete, distributing frame cost
        int waitFrames = 0;
        while (!marchingHandle.IsCompleted)
        {
            waitFrames++;
            // Only yield every few frames for short-running jobs
            if (waitFrames > 2)
            {
                yield return null;
                waitFrames = 0;
            }
        }
        
        try
        {
            // Complete the job
            marchingCubesAllocator.CompleteCurrentJob();
            timing.EndPhase("MarchingCubesJob");
        }
        catch (Exception e)
        {
            marchingCubesFailed = true;
            Debug.LogError($"Error completing marching cubes job for chunk {chunkData.ChunkCoordinate}: {e.Message}");
            QuarantineChunk($"Marching cubes job completion failed: {e.Message}");
        }
        
        if (marchingCubesFailed)
        {
            if (vertexCounter.IsCreated) vertexCounter.Dispose();
            if (triangleCounter.IsCreated) triangleCounter.Dispose();
            timing.EndAllPhases();
            generationCoroutine = null;
            yield break;
        }

        // Yield again to distribute frame cost
        yield return null;

        // STEP 4: Create mesh
        Vector3[] vertices = null;
        int[] triangles = null;
        
        timing.StartPhase("MeshGeneration");
        
        // Safe check for meshFilter
        if (meshFilter == null)
        {
            meshFilter = gameObject.GetComponent<MeshFilter>();
            if (meshFilter == null)
            {
                meshFilter = gameObject.AddComponent<MeshFilter>();
            }
        }

        Mesh mesh = null;
        
        mesh = meshFilter.sharedMesh;
        if (mesh == null)
        {
            mesh = MeshDataPool.Instance.GetMesh();
            meshFilter.sharedMesh = mesh;
        }
        else
        {
            mesh.Clear();
        }
        
        // Extract data from job
        int vertexCount = 0;
        int triangleCount = 0;
        
        // Safe to access counters after job completion
        vertexCount = vertexCounter[0];
        triangleCount = triangleCounter[0];

        // Clean up native arrays
        vertexCounter.Dispose();
        triangleCounter.Dispose();
        
        // Check if we have any vertices/triangles to process
        if (vertexCount > 0 && triangleCount > 0)
        {
            // Request arrays from the pool with proper sizes
            vertices = ChunkArrayPool.GetVectorBuffer(vertexCount);
            triangles = ChunkArrayPool.GetIndexBuffer(triangleCount);
            
            // Copy data from the buffers
            int validVertices = Math.Min(vertexCount, marchingCubesAllocator.VertexBuffer.Length);
            int validTriangles = Math.Min(triangleCount, marchingCubesAllocator.TriangleBuffer.Length);
            
            // Make sure we don't access past the end of arrays
            for (int i = 0; i < validVertices; i++)
            {
                float3 vertex = marchingCubesAllocator.VertexBuffer[i];
                vertices[i] = new Vector3(
                    vertex.x * chunkData.VoxelSize,
                    vertex.y * chunkData.VoxelSize,
                    vertex.z * chunkData.VoxelSize
                );
            }

            // Process triangles in batches to avoid long stalls
            int batchSize = 5000;
            for (int i = 0; i < validTriangles; i += batchSize)
            {
                int count = Mathf.Min(batchSize, validTriangles - i);
                
                for (int j = 0; j < count; j++)
                {
                    // Ensure we're not using invalid vertex indices
                    int index = marchingCubesAllocator.TriangleBuffer[i + j];
                    if (index < vertexCount)
                    {
                        triangles[i + j] = index;
                    }
                    else
                    {
                        // Use a safe fallback - this will create degenerate triangles
                        // but prevent crashes
                        triangles[i + j] = 0;
                    }
                }
                
                // Yield every batch to spread the work
                if (i + count < validTriangles)
                {
                    yield return null;
                }
            }
            
            // STEP 5: Apply mesh
            timing.StartPhase("MeshApplication");
            
            ApplyMesh(vertices, triangles);
            
            timing.EndPhase("MeshApplication");
        }
        else
        {      
            // Create empty arrays to prevent null references
            vertices = new Vector3[0];
            triangles = new int[0];
            
            // Apply empty mesh
            ApplyMesh(vertices, triangles);
        }
        
        // Always clean up resources
        if (vertices != null) ChunkArrayPool.ReturnVectorBuffer(vertices);
        if (triangles != null) ChunkArrayPool.ReturnIndexBuffer(triangles);
        
        timing.EndPhase("MeshGeneration");
        marchingCubesComplete = true;

        timing.EndAllPhases();
        
        if (EnhancedBenchmarkManager.Instance != null)
        {
            EnhancedBenchmarkManager.Instance.EndOperation(chunkData.ChunkCoordinate);
        }

        // Cleanup and state updates
        generationCoroutine = null;
        UpdateAccessTime();
        
        // Call OnGenerationComplete to handle state transitions and other clean-up tasks
        OnGenerationComplete();

        // Process any pending voxel updates if they weren't handled by OnGenerationComplete
        if (pendingVoxelUpdates.Count > 0)
        {
            SendPendingUpdatesToWorld(true);
        }
    }

    private IEnumerator DelayedUnloadRequest(Vector3Int chunkCoord, float delay)
    {
        yield return new WaitForSeconds(delay);
        
        // Check again if there are pending updates before unloading
        if (!World.Instance.HasPendingUpdates(chunkCoord))
        {
            // Check current state to ensure we don't unload in an invalid state
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            if (state.Status == ChunkConfigurations.ChunkStatus.Loaded || 
                state.Status == ChunkConfigurations.ChunkStatus.Modified)
            {
                if (ChunkLifecycleLogsEnabled)
                {
                    Debug.Log($"Delayed unload request for empty/solid chunk {chunkCoord}");
                }
                ChunkOperationsQueue.Instance.QueueChunkForUnload(chunkCoord);
            }
        }
    }

    private void QuarantineChunk(string reason)
    {
        var chunkCoord = chunkData?.ChunkCoordinate ?? 
            Coord.WorldToChunkCoord(transform.position, World.Instance.chunkSize, World.Instance.voxelSize);
        
        var currentState = ChunkStateManager.Instance.GetChunkState(chunkCoord).Status;
        World.Instance.QuarantineChunk(chunkCoord, reason, currentState);
    }

    private void OnGenerationComplete()
    {
        // Process any queued generation request
        if (isGenerationQueued)
        {
            isGenerationQueued = false;
            QueueGeneration(false, false);
        }

        // Process any pending voxel updates
        if (pendingVoxelUpdates.Count > 0)
        {
            SendPendingUpdatesToWorld(true);
        }

        Vector3Int chunkCoord = Coord.WorldToChunkCoord(transform.position, chunkData.TotalPointsPerAxis - 1, chunkData.VoxelSize);
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);

        if (densityGenerationComplete && marchingCubesComplete)
        {
            // For loaded chunks with modifications, transition to Modified state
            if (chunkData.HasModifiedData)
            {
                if (state.Status != ChunkConfigurations.ChunkStatus.Modified)
                {
                    ChunkStateManager.Instance.TryChangeState(
                        chunkCoord,
                        ChunkConfigurations.ChunkStatus.Modified,
                        ChunkConfigurations.ChunkStateFlags.Active
                    );
                    
                    // Immediately save modified data
                    chunkData.SaveData();
                }
            }
            // For fresh or unmodified chunks, ensure they're in Loaded state
            else if (state.Status == ChunkConfigurations.ChunkStatus.Loading)
            {
                // This is the normal case - transitioning from Loading to Loaded
                World.Instance.OnChunkGenerationComplete(chunkCoord);
                
                // No need to explicitly change state as OnChunkGenerationComplete does this
            }
            else if (state.Status == ChunkConfigurations.ChunkStatus.Loaded)
            {
                // The chunk is already in Loaded state - this is fine, no warning needed
                if (ChunkLifecycleLogsEnabled)
                {
                    Debug.Log($"Chunk {chunkCoord} generation complete, already in Loaded state");
                }
            }
            else if (state.Status != ChunkConfigurations.ChunkStatus.Modified)
            {
                // Only log a warning for truly unexpected states
                Debug.LogWarning($"Chunk {chunkCoord} completed generation but is in unexpected state: {state.Status}");
            }
        }

        if (EnhancedBenchmarkManager.Instance != null)
        {
            EnhancedBenchmarkManager.Instance.EndOperation(chunkCoord);
        }
        UpdateAccessTime();
    }

    public bool TrySetDensityPoint(Vector3Int pointCoord, float newDensity)
    {
        // Ensure arrays are created
        EnsureDataInitialized();
        CompleteAllJobs();
        
        if (!Coord.IsDensityPositionValid(pointCoord, chunkData?.TotalPointsPerAxis ?? 0))
        {
            return false;
        }

        try
        {
            int index = Coord.GetDensityPointIndex(pointCoord, chunkData.TotalPointsPerAxis);
            if (index >= 0 && index < chunkData.DensityPoints.Length)
            {
                chunkData.SetDensityPoint(index, new DensityPoint(new float3(pointCoord.x, pointCoord.y, pointCoord.z), newDensity));
                
                // Try to update the corresponding voxel
                Vector3Int voxelToUpdate = pointCoord - Vector3Int.one;
                if (Coord.IsVoxelPositionValid(voxelToUpdate, chunkData.TotalPointsPerAxis - 1))
                {
                    UpdateVoxelDataAtPosition(voxelToUpdate);
                }
                UpdateAccessTime();
                return true;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error in TrySetDensityPoint for {pointCoord}: {e.Message}");
        }
        
        return false;
    }

    // Data access methods
    public void SetDensityPoint(Vector3Int pointCoord, float newDensity)
    {
        // Ensure arrays are created
        EnsureDataInitialized();
        CompleteAllJobs();
        
        // Early exit with detailed warning if position is invalid
        if (!Coord.IsDensityPositionValid(pointCoord, chunkData?.TotalPointsPerAxis ?? 0))
        {
            Debug.LogWarning($"Invalid density position: {pointCoord} for chunk {chunkData?.ChunkCoordinate}, totalPoints: {chunkData?.TotalPointsPerAxis}");
            return;
        }

        int index = Coord.GetDensityPointIndex(pointCoord, chunkData.TotalPointsPerAxis);
        
        // Double-check index before accessing array
        if (index >= 0 && index < chunkData.DensityPoints.Length)
        {
            chunkData.SetDensityPoint(index, new DensityPoint(new float3(pointCoord.x, pointCoord.y, pointCoord.z), newDensity));
            
            // Update corresponding voxel with bounds check first
            Vector3Int voxelToUpdate = pointCoord - Vector3Int.one;
            if (Coord.IsVoxelPositionValid(voxelToUpdate, chunkData.TotalPointsPerAxis - 1))
            {
                UpdateVoxelDataAtPosition(voxelToUpdate);
            }
        }
        else
        {
            Debug.LogError($"Density point index {index} out of range (max: {chunkData.DensityPoints.Length - 1}, coords: {pointCoord})");
        }
    }

    public float GetDensityAtPosition(Vector3Int densityPosition)
    {
        if (Coord.IsDensityPositionValid(densityPosition, chunkData.TotalPointsPerAxis))
        {
            int index = Coord.GetDensityPointIndex(densityPosition, chunkData.TotalPointsPerAxis);
            return chunkData.DensityPoints[index].density;
        }
        return 1f; // Return value indicating outside surface
    }

    /// <summary>
    /// Initialize density arrays for QuickCheck chunks that have pending updates.
    /// This ensures density arrays have correct baseline values before modifications are applied.
    /// </summary>
    public void InitializeQuickCheckChunkDensity()
    {
        // CRITICAL: Check if chunkData exists before accessing it
        if (chunkData == null)
        {
            Debug.LogError($"[InitializeQuickCheckChunkDensity] chunkData is null - cannot initialize");
            return;
        }
        
        EnsureDataInitialized();
        CompleteAllJobs();
        
        if (!chunkData.DensityPoints.IsCreated)
        {
            Debug.LogError($"[InitializeQuickCheckChunkDensity] Density array not created for chunk {chunkData.ChunkCoordinate}");
            return;
        }
        
        bool isEmpty = chunkData.IsEmptyChunk;
        bool isSolid = chunkData.IsSolidChunk;
        
        if (!isEmpty && !isSolid)
        {
            // Not a QuickCheck chunk, skip initialization
            return;
        }
        
        int totalPointsPerAxis = chunkData.TotalPointsPerAxis;
        float baselineDensity;
        
        if (isSolid)
        {
            // Solid chunks: all density values should be above surfaceLevel (outside surface)
            baselineDensity = chunkData.SurfaceLevel + 2.0f;
            Debug.Log($"[InitializeQuickCheckChunkDensity] Initializing SOLID chunk {chunkData.ChunkCoordinate} with baseline density {baselineDensity}");
        }
        else // isEmpty
        {
            // Empty chunks: all density values should be below surfaceLevel (inside/under surface)
            baselineDensity = chunkData.SurfaceLevel - 2.0f;
            Debug.Log($"[InitializeQuickCheckChunkDensity] Initializing EMPTY chunk {chunkData.ChunkCoordinate} with baseline density {baselineDensity}");
        }
        
        // Initialize all density points with baseline value
        for (int x = 0; x < totalPointsPerAxis; x++)
        for (int y = 0; y < totalPointsPerAxis; y++)
        for (int z = 0; z < totalPointsPerAxis; z++)
        {
            Vector3Int densityPos = new Vector3Int(x, y, z);
            int index = Coord.GetDensityPointIndex(densityPos, totalPointsPerAxis);
            
            if (index >= 0 && index < chunkData.DensityPoints.Length)
            {
                // Only initialize if density is at default/uninitialized value (0 or very close to 0)
                // This prevents overwriting already-initialized values
                float currentDensity = chunkData.DensityPoints[index].density;
                if (Mathf.Abs(currentDensity) < 0.01f)
                {
                    chunkData.SetDensityPoint(index, new DensityPoint(new float3(x, y, z), baselineDensity));
                }
            }
        }
        
        Debug.Log($"[InitializeQuickCheckChunkDensity] Completed initialization for {(isSolid ? "SOLID" : "EMPTY")} chunk {chunkData.ChunkCoordinate}");
    }

    public Voxel GetVoxel(Vector3Int voxelPosition)
    {
        if (Coord.IsVoxelPositionValid(voxelPosition, chunkData.TotalPointsPerAxis - 1))
        {
            int index = Coord.GetVoxelIndex(voxelPosition, chunkData.TotalPointsPerAxis - 1);
            return chunkData.VoxelData[index];
        }
        return new Voxel(VOXEL_INVALID, -1);
    }

    public void UpdateVoxelDataAtPosition(Vector3Int voxelPosition)
    {
        if (!Coord.IsVoxelPositionValid(voxelPosition, chunkData.TotalPointsPerAxis - 1)) 
            return;

        Vector3Int densityPosition = Coord.VoxelToDensityCoord(voxelPosition);
        bool shouldBeActive = false;
        float totalDensity = 0f;
        int samples = 0;

        // Sample more points within the voxel
        for (float dx = 0; dx <= 1; dx += 0.5f)
        for (float dy = 0; dy <= 1; dy += 0.5f)
        for (float dz = 0; dz <= 1; dz += 0.5f)
        {
            Vector3Int samplePos = densityPosition + new Vector3Int(
                Mathf.FloorToInt(dx), 
                Mathf.FloorToInt(dy), 
                Mathf.FloorToInt(dz));
                
            if (Coord.IsDensityPositionValid(samplePos, chunkData.TotalPointsPerAxis))
            {
                float density = GetDensityAtPosition(samplePos);
                totalDensity += density;
                samples++;
                
                if (density < chunkData.SurfaceLevel)
                {
                    shouldBeActive = true;
                }
            }
        }

        int index = Coord.GetVoxelIndex(voxelPosition, chunkData.TotalPointsPerAxis - 1);
        Voxel currentVoxel = chunkData.VoxelData[index];
        
        if (shouldBeActive != (currentVoxel.isActive == VOXEL_ACTIVE) || 
            Mathf.Abs(totalDensity/samples - chunkData.SurfaceLevel) < 0.1f)
        {
            Voxel newVoxel = new Voxel(
                shouldBeActive ? VOXEL_ACTIVE : VOXEL_INACTIVE,
                shouldBeActive ? 3 : 0
            );
            chunkData.SetVoxel(index, newVoxel);
            isMeshUpdateQueued = true;
        }
    }

    public void AddVoxel(Vector3Int voxelPosition)
    {
        if (Coord.IsVoxelPositionValid(voxelPosition, chunkData.TotalPointsPerAxis - 1))
        {
            int index = Coord.GetVoxelIndex(voxelPosition, chunkData.TotalPointsPerAxis - 1);
            
            // Use default voxel health from config
            float defaultHealth = World.Instance.Config.baseDensityStrength;
            chunkData.SetVoxel(index, new Voxel(VOXEL_ACTIVE, defaultHealth));
            isMeshUpdateQueued = true;
            
            World.Instance.HandleVoxelDestruction(chunkData.ChunkCoordinate, voxelPosition);
            
            pendingVoxelUpdates.Add(new PendingVoxelUpdate(voxelPosition, isAdding: true, propagate: true));
            SendPendingUpdatesToWorld(true);
            
            // Handle modification state
            HandleModificationState();
        }
    }

    public void DamageVoxel(Vector3Int voxelPosition, int damageAmount)
    {
        if (!Coord.IsVoxelPositionValid(voxelPosition, chunkData?.TotalPointsPerAxis - 1 ?? 0))
        {
            Debug.LogError($"Invalid voxel position: {voxelPosition}");
            return;
        }

        var state = ChunkStateManager.Instance.GetChunkState(chunkData?.ChunkCoordinate ?? Vector3Int.zero);
        if (state.Status != ChunkConfigurations.ChunkStatus.Loaded && 
            state.Status != ChunkConfigurations.ChunkStatus.Modified)
        {
            Debug.LogWarning($"Cannot damage voxel - chunk in invalid state: {state.Status}");
            return;
        }

        // Ensure arrays are created
        EnsureDataInitialized();
        CompleteAllJobs();
        UpdateAccessTime();
        // Get voxel state before change
        int index = Coord.GetVoxelIndex(voxelPosition, chunkData.TotalPointsPerAxis - 1);
        var currentVoxel = chunkData.VoxelData[index];

        // Check if this is a solid chunk (all voxels active)
        bool wasSolidChunk = false;
        if (TerrainAnalysisCache.TryGetAnalysis(chunkData.ChunkCoordinate, out var analysis) && analysis.IsSolid)
        {
            wasSolidChunk = true;
            if (ChunkLifecycleLogsEnabled)
            {
                Debug.Log($"Processing voxel in solid chunk {chunkData.ChunkCoordinate} - position {voxelPosition}");
            }
        }

        // Process the damage - special handling for solid chunks
        SetVoxelInactive(voxelPosition);
        
        // For solid chunks, update surrounding voxels and density field more aggressively
        if (wasSolidChunk)
        {
            // For solid chunks, update a box of voxels around the target
            int radius = 1; // Smaller radius since this is just for immediate voxels
            
            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                Vector3Int neighborPos = voxelPosition + new Vector3Int(dx, dy, dz);
                
                // Skip if out of bounds or it's the center voxel we already processed
                if (!Coord.IsVoxelPositionValid(neighborPos, chunkData.TotalPointsPerAxis - 1) ||
                    (dx == 0 && dy == 0 && dz == 0))
                    continue;
                    
                // Get the index and current state
                int neighborIndex = Coord.GetVoxelIndex(neighborPos, chunkData.TotalPointsPerAxis - 1);
                
                // Set neighbor voxels inactive too
                chunkData.SetVoxel(neighborIndex, new Voxel(VOXEL_INACTIVE, 0));
                
                // Also ensure we update the corresponding density points
                Vector3Int densityPos = Coord.VoxelToDensityCoord(neighborPos);
                if (Coord.IsDensityPositionValid(densityPos, chunkData.TotalPointsPerAxis))
                {
                    // Set density higher than threshold
                    float newDensity = chunkData.SurfaceLevel + 0.5f;
                    int densityIndex = Coord.GetDensityPointIndex(densityPos, chunkData.TotalPointsPerAxis);
                    chunkData.SetDensityPoint(densityIndex, new DensityPoint(
                        new float3(densityPos.x, densityPos.y, densityPos.z), 
                        newDensity
                    ));
                }
            }
        }
        
        if (state.Status == ChunkConfigurations.ChunkStatus.Loaded)
        {
            ChunkStateManager.Instance.TryChangeState(
                chunkData.ChunkCoordinate,
                ChunkConfigurations.ChunkStatus.Modified,
                ChunkConfigurations.ChunkStateFlags.Active
            );
        }

        // Tell World to handle density updates
        World.Instance.HandleVoxelDestruction(chunkData.ChunkCoordinate, voxelPosition);

        // Queue mesh update
        Generate(log: false, fullMesh: false, quickCheck: false);
    }

    public void SetVoxelDirect(Vector3Int position, int isActive, float hitpoints)
    {
        if (Coord.IsVoxelPositionValid(position, chunkData.TotalPointsPerAxis - 1))
        {
            int index = Coord.GetVoxelIndex(position, chunkData.TotalPointsPerAxis - 1);
            chunkData.SetVoxel(index, new Voxel(isActive, hitpoints));
            isMeshUpdateQueued = true;
            UpdateAccessTime();
        }
    }

    public void SetVoxelInactive(Vector3Int voxelPosition)
    {
        if (Coord.IsVoxelPositionValid(voxelPosition, chunkData.TotalPointsPerAxis - 1))
        {
            int index = Coord.GetVoxelIndex(voxelPosition, chunkData.TotalPointsPerAxis - 1);
            chunkData.SetVoxel(index, new Voxel(VOXEL_INACTIVE, 0));
        }
    }

    private void HandleModificationState()
    {
        var state = ChunkStateManager.Instance.GetChunkState(chunkData.ChunkCoordinate);
        
        // Only proceed if we're in a Loaded or Modified state
        if (state.Status != ChunkConfigurations.ChunkStatus.Loaded && 
            state.Status != ChunkConfigurations.ChunkStatus.Modified)
        {
            return;
        }

        // If we've made modifications, transition to Modified state
        if (chunkData.HasModifiedData && state.Status == ChunkConfigurations.ChunkStatus.Loaded)
        {
            ChunkStateManager.Instance.TryChangeState(
                chunkData.ChunkCoordinate,
                ChunkConfigurations.ChunkStatus.Modified,
                ChunkConfigurations.ChunkStateFlags.Active
            );
        }
    }

    public void SendPendingUpdatesToWorld(bool propagate)
    {
        if (pendingVoxelUpdates.Count > 0)
        {
            foreach (var voxelUpdate in pendingVoxelUpdates)
            {
                World.Instance.QueueVoxelUpdate(
                    chunkData.ChunkCoordinate, 
                    voxelUpdate.voxelPosition, 
                    voxelUpdate.isAdding, 
                    propagate
                );
            }
            pendingVoxelUpdates.Clear();
        }
    }
    
    private void ApplyMesh(Vector3[] vertices, int[] triangles)
    {
        // Check for null or empty data early
        if (vertices == null || triangles == null)
        {
            Debug.LogError($"Null vertex or triangle arrays for chunk {chunkData?.ChunkCoordinate}");
            if (meshCollider != null)
            {
                meshCollider.enabled = false;
            }
            return;
        }
        
        if (vertices.Length < 3 || triangles.Length < 3)
        {
            // This is normal for empty chunks - don't log it as an error
            if (meshCollider != null)
            {
                meshCollider.enabled = false;
            }
            return;
        }

        // Get or create mesh
        Mesh mesh = null;
        Vector2[] uvs = null;

        // Get mesh safely
        if (meshFilter == null)
        {
            meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null)
            {
                meshFilter = gameObject.AddComponent<MeshFilter>();
            }
        }
        
        mesh = meshFilter.sharedMesh;
        if (mesh == null)
        {
            mesh = MeshDataPool.Instance.GetMesh();
            if (mesh == null)
            {
                Debug.LogError($"Failed to get mesh from pool for chunk {chunkData?.ChunkCoordinate}");
                return;
            }
            meshFilter.sharedMesh = mesh;
        }
        else
        {
            mesh.Clear();
        }

        // Direct mesh application approach
        try {
            // First set the vertices - this establishes the array size
            mesh.vertices = vertices;
            
            // Now generate UVs to match the exact vertex count
            uvs = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 worldPos = vertices[i] + transform.position;
                uvs[i] = new Vector2(
                    (worldPos.x + worldPos.z) * 0.1f, 
                    (worldPos.y + worldPos.z) * 0.1f
                );
            }
            
            // Set UVs and triangles
            mesh.uv = uvs;
            mesh.triangles = triangles;
            
            // Recalculate mesh properties
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            
            // Check for valid bounds
            var bounds = mesh.bounds;
            if (bounds.size == Vector3.zero || bounds.extents.magnitude > 10000f)
            {
                Debug.LogWarning($"Invalid mesh bounds for chunk {chunkData?.ChunkCoordinate}: {bounds}");
                if (meshCollider != null)
                {
                    meshCollider.enabled = false;
                }
                return;
            }
            
            // Apply collider
            if (meshCollider == null)
            {
                meshCollider = gameObject.AddComponent<MeshCollider>();
            }
            
            meshCollider.sharedMesh = null;
            meshCollider.sharedMesh = mesh;
            meshCollider.enabled = true;
            
            // Apply material
            if (meshRenderer == null)
            {
                meshRenderer = GetComponent<MeshRenderer>();
                if (meshRenderer == null)
                {
                    meshRenderer = gameObject.AddComponent<MeshRenderer>();
                }
            }
            
            // Set material if available
            if (World.Instance?.VoxelMaterial != null)
            {
                meshRenderer.sharedMaterial = World.Instance.VoxelMaterial;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error applying mesh for chunk {chunkData?.ChunkCoordinate}: {e.Message}\n{e.StackTrace}");
            if (meshCollider != null)
            {
                meshCollider.enabled = false;
            }
        }
    }

    public void SaveModifiedData()
    {
        if (chunkData == null || !chunkData.HasModifiedData)
            return;
        
        // Ensure all jobs are completed
        CompleteAllJobs();
        
        // Update state if needed and hasn't been done already
        var state = ChunkStateManager.Instance.GetChunkState(chunkData.ChunkCoordinate);
        if (state.Status == ChunkConfigurations.ChunkStatus.Loaded)
        {
            ChunkStateManager.Instance.TryChangeState(
                chunkData.ChunkCoordinate,
                ChunkConfigurations.ChunkStatus.Modified,
                ChunkConfigurations.ChunkStateFlags.Active
            );
        }
        
        // Force save the chunk data
        if (ChunkLifecycleLogsEnabled)
        {
            Debug.Log($"Saving modified data for chunk {chunkData.ChunkCoordinate}");
        }
        chunkData.SaveData();
    }

    private Voxel SafeGetVoxel(int index)
    {
        CompleteAllJobs();
        return chunkData.VoxelData[index];
    }

    public void ResetChunk()
    {
        CompleteAllJobs();
        
        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            MeshDataPool.Instance.ReturnMesh(meshFilter.sharedMesh);
            meshFilter.sharedMesh = null;
        }

        if (meshCollider != null)
        {
            DestroyImmediate(meshCollider);
            meshCollider = null;
        }

        if (chunkData != null)
        {
            chunkData.Reset();
        }

        IsInitialized = false;
        isMeshUpdateQueued = false;
        isGenerationQueued = false;
        isDisposed = false;
    }

    public void CompleteAllJobs()
    {
        try
        {
            if (generationCoroutine != null)
            {
                StopCoroutine(generationCoroutine);
                generationCoroutine = null;
            }

            if (densityJobScheduled)
            {
                densityHandle.Complete();
                densityJobScheduled = false;
            }

            if (marchingCubesAllocator != null && marchingCubesAllocator.IsCreated)
            {
                // Force completion of any running marching cubes job
                marchingCubesAllocator.CompleteCurrentJob();
                
                // Double check to ensure no jobs are still running
                if (marchingCubesAllocator.IsJobRunning)
                {
                    Debug.LogWarning($"Forcing completion of stubborn marching cubes job on chunk {chunkData.ChunkCoordinate}");
                    marchingCubesAllocator.CompleteCurrentJob();
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error completing jobs on chunk {chunkData.ChunkCoordinate}: {e.Message}\n{e.StackTrace}");
            throw;
        }
    }

    private void OnDestroy()
    {
        if (hasQuit) return;
/* 
        if (networkObject != null && networkObject.IsSpawned)
        {
            networkObject.Despawn();
        }
*/
        CompleteAllJobs();
        
        if (!isDisposed)
        {
            DisposeNativeContainers();
            isDisposed = true;
        }

        if (chunkData != null)
        {
            chunkData.Dispose();
        }

        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            MeshDataPool.Instance.ReturnMesh(meshFilter.sharedMesh);
            meshFilter.sharedMesh = null;
        }

        if (marchingCubesAllocator != null && marchingCubesAllocator.IsCreated)
        {
            marchingCubesAllocator.Dispose();
            marchingCubesAllocator = null;
        }
    }

    private void OnApplicationQuit()
    {
        hasQuit = true;
        
        // Complete any pending jobs
        CompleteAllJobs();
        
        // Clean up resources
        if (!isDisposed)
        {
            DisposeNativeContainers();
            isDisposed = true;
        }

        if (chunkData != null)
        {
            chunkData.Dispose();
        }

        // Handle mesh cleanup
        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            MeshDataPool.Instance.ReturnMesh(meshFilter.sharedMesh);
            meshFilter.sharedMesh = null;
        }

        if (marchingCubesAllocator != null && marchingCubesAllocator.IsCreated)
        {
            marchingCubesAllocator.Dispose();
            marchingCubesAllocator = null;
        }
    }
}