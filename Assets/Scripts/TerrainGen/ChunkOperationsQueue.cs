//v1.0.1
//REPLACE ENTIRE FILE
using System;
using System.Collections.Generic;
using UnityEngine;
using NelsUtils;
using System.Linq;
using Unity.Netcode;

public class ChunkOperationsQueue : MonoBehaviour
{
    public static ChunkOperationsQueue Instance { get; private set; }

    // Operation type enum
    public enum OperationType { Load, Unload, Modify, Save, Generate }

    // Operation priority
    public enum OperationPriority { Low = 0, Normal = 1, High = 2, Critical = 3 }
    // Operation state with improved error tracking
    public class ChunkOperation
    {
        public Vector3Int ChunkCoordinate { get; }
        public OperationType Type { get; }
        public OperationPriority Priority { get; }
        public bool QuickCheck { get; set; }
        public object Parameters { get; }
        public DateTime QueueTime { get; }
        public ChunkConfigurations.ChunkStatus RequiredInitialStatus { get; }
        public ChunkConfigurations.ChunkStatus TargetStatus { get; }
        public int RetryCount { get; set; }
        public List<string> ErrorHistory { get; } = new List<string>();

        public ChunkOperation(
            Vector3Int coordinate, 
            OperationType type, 
            OperationPriority priority,
            bool quickCheck,
            ChunkConfigurations.ChunkStatus requiredStatus,
            ChunkConfigurations.ChunkStatus targetStatus,
            object parameters = null)
        {
            ChunkCoordinate = coordinate;
            Type = type;
            Priority = priority;
            QuickCheck = quickCheck;
            Parameters = parameters;
            QueueTime = DateTime.UtcNow;
            RequiredInitialStatus = requiredStatus;
            TargetStatus = targetStatus;
            RetryCount = 0;
        }
    }

    // Optimized queue structure using priority buckets
    private readonly Dictionary<OperationPriority, Queue<ChunkOperation>> operationQueues = 
        new Dictionary<OperationPriority, Queue<ChunkOperation>>();

    // Active operations tracking
    private readonly HashSet<Vector3Int> chunksInProcess = new HashSet<Vector3Int>();
    private readonly Dictionary<Vector3Int, ChunkOperation> activeOperations = 
        new Dictionary<Vector3Int, ChunkOperation>();

    // Enhanced error tracking
    private readonly Dictionary<Vector3Int, List<ChunkConfigurations.ChunkError>> errorHistory = 
        new Dictionary<Vector3Int, List<ChunkConfigurations.ChunkError>>();

    // Load/Unload queue optimization
    private readonly HashSet<Vector3Int> loadingChunks = new HashSet<Vector3Int>();
    private readonly HashSet<Vector3Int> unloadingChunks = new HashSet<Vector3Int>();
    private readonly Queue<Vector3Int> loadQueue = new Queue<Vector3Int>();
    private readonly Queue<Vector3Int> unloadQueue = new Queue<Vector3Int>();

    private Dictionary<Vector3Int, OperationPriority> _loadRequests 
    = new Dictionary<Vector3Int, OperationPriority>();
    private HashSet<Vector3Int> chunksWithPendingLoads = new HashSet<Vector3Int>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        InitializeQueues();
    }

    private void InitializeQueues()
    {
        foreach (OperationPriority priority in Enum.GetValues(typeof(OperationPriority)))
        {
            operationQueues[priority] = new Queue<ChunkOperation>();
        }
    }

    public int GetQueueSize()
    {
        int total = 0;
        foreach (var queue in operationQueues.Values)
        {
            total += queue.Count;
        }
        total += loadQueue.Count + unloadQueue.Count;

        total += activeOperations.Count;

        return total;
    }

    public bool HasPendingUnloadOperations()
    {
        if (unloadQueue.Count > 0 || unloadingChunks.Count > 0)
            return true;

        if (activeOperations.Any(pair => pair.Value.Type == OperationType.Unload))
            return true;

        foreach (var queue in operationQueues.Values)
        {
            if (queue.Any(op => op.Type == OperationType.Unload))
            {
                return true;
            }
        }

        return false;
    }

    public bool HasPendingLoadOperation(Vector3Int chunkCoord)
    {
        return chunksWithPendingLoads.Contains(chunkCoord);
    }
    
    private bool HasHigherPriorityLoadOperation(Vector3Int chunkCoord, bool immediate, bool quickCheck)
    {
        foreach (var queue in operationQueues.Values)
        {
            foreach (var op in queue)
            {
                if (op.ChunkCoordinate == chunkCoord && op.Type == OperationType.Load)
                {
                    // If existing operation is immediate and not quick check, it has higher priority
                    if (!op.QuickCheck && op.Priority == OperationPriority.High)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public void QueueChunkForLoad(Vector3Int chunkCoord, bool immediate = false, bool quickCheck = true)
    {
        // Check if being overridden because it's a solid chunk that needs modification
        bool isSolidModified = false;
        
        // CRITICAL: Add this check to ensure we don't use quick check for solid modified chunks
        if (World.Instance != null && World.Instance.IsSolidChunkMarkedForModification(chunkCoord))
        {
            isSolidModified = true;
            quickCheck = false; // Disable quickCheck for modified solid chunks
            Debug.Log($"[ChunkQueue] Forcing full load of modified solid chunk {chunkCoord}");
        }
        // Skip if chunk is quarantined
        if (ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord))
        {
            Debug.LogWarning($"[ChunkQueue] Skipping load request for quarantined chunk {chunkCoord}");
            return;
        }

        // Get current chunk state
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);

        // Skip if chunk is already loaded or in queue
        if (World.Instance.TryGetChunk(chunkCoord, out _))
        {
            return;
        }

        // Check if we already have a pending load operation
        if (HasPendingLoadOperation(chunkCoord))
        {
            return;
        }

        // Figure out the new priority
        var newPriority = immediate 
            ? OperationPriority.Critical 
            : OperationPriority.Normal;

        // Create and enqueue the operation
        var operation = new ChunkOperation(
            chunkCoord,
            OperationType.Load,
            newPriority,
            isSolidModified ? false : quickCheck, // Use appropriate quickCheck value
            GetRequiredInitialStatus(OperationType.Load),
            GetTargetStatus(OperationType.Load)
        );

        EnqueueOperation(operation);

        if (World.Instance != null)
        {
            World.Instance.OnInitialChunkLoadQueued(chunkCoord);
        }
    }

    private void RemoveExistingLoadRequest(Vector3Int chunkCoord)
    {
        // Remove from the dictionary
        if (_loadRequests.ContainsKey(chunkCoord))
            _loadRequests.Remove(chunkCoord);

        // Also remove any existing op from the actual operationQueues so we don’t
        // wind up with two loads for the same chunk. We can do a quick pass over
        // all priorities, but we won't attempt a full rebuild—just remove the one operation.
        foreach (var queue in operationQueues.Values)
        {
            // We do a single pass of the queue and rebuild it
            // This is still more efficient because we only do it
            // when we’re "upgrading" a chunk’s load priority.
            var tempQueue = new Queue<ChunkOperation>(queue.Count);
            while (queue.Count > 0)
            {
                var op = queue.Dequeue();
                if (op.ChunkCoordinate == chunkCoord && op.Type == OperationType.Load)
                {
                    // skip
                }
                else
                {
                    tempQueue.Enqueue(op);
                }
            }
            // re-assign
            while (tempQueue.Count > 0)
                queue.Enqueue(tempQueue.Dequeue());
        }
    }
    
    public void QueueChunkForUnload(Vector3Int chunkCoord)
    {
        if (unloadingChunks.Contains(chunkCoord))
            return;

        if (loadQueue.Contains(chunkCoord))
        {
            RemoveFromLoadQueue(chunkCoord);
            loadingChunks.Remove(chunkCoord);
        }

        unloadQueue.Enqueue(chunkCoord);
        unloadingChunks.Add(chunkCoord);
    }

    private void RemoveFromLoadQueue(Vector3Int chunkCoord)
    {
        var tempQueue = new Queue<Vector3Int>();
        while (loadQueue.Count > 0)
        {
            var coord = loadQueue.Dequeue();
            if (coord != chunkCoord)
                tempQueue.Enqueue(coord);
        }
        while (tempQueue.Count > 0)
            loadQueue.Enqueue(tempQueue.Dequeue());
    }

    private void RemoveFromUnloadQueue(Vector3Int chunkCoord)
    {
        var tempQueue = new Queue<Vector3Int>();
        while (unloadQueue.Count > 0)
        {
            var coord = unloadQueue.Dequeue();
            if (coord != chunkCoord)
                tempQueue.Enqueue(coord);
        }
        while (tempQueue.Count > 0)
            unloadQueue.Enqueue(tempQueue.Dequeue());
        unloadingChunks.Remove(chunkCoord);
    }

    private ChunkConfigurations.ChunkStatus GetRequiredInitialStatus(OperationType type)
    {
        switch (type)
        {
            case OperationType.Load:
                // For Load operations, accept either None or Unloaded states
                return ChunkConfigurations.ChunkStatus.None;
            case OperationType.Unload:
                return ChunkConfigurations.ChunkStatus.Loaded;
            case OperationType.Modify:
                return ChunkConfigurations.ChunkStatus.Loaded;
            case OperationType.Save:
                return ChunkConfigurations.ChunkStatus.Modified;
            case OperationType.Generate:
                return ChunkConfigurations.ChunkStatus.Loading;
            default:
                throw new ArgumentException($"Unknown operation type: {type}");
        }
    }

    private ChunkConfigurations.ChunkStatus GetTargetStatus(OperationType type)
    {
        switch (type)
        {
            case OperationType.Load:
                return ChunkConfigurations.ChunkStatus.Loaded;
            case OperationType.Unload:
                return ChunkConfigurations.ChunkStatus.Unloaded;
            case OperationType.Modify:
                return ChunkConfigurations.ChunkStatus.Modified;
            case OperationType.Save:
                return ChunkConfigurations.ChunkStatus.Saved;
            case OperationType.Generate:
                return ChunkConfigurations.ChunkStatus.Loaded;
            default:
                throw new ArgumentException($"Unknown operation type: {type}");
        }
    }

    public void LogOperationFailure(ChunkOperation operation, string reason, string operationName = null)
    {
        var error = new ChunkConfigurations.ChunkError
        {
            Message = $"{operationName ?? operation.Type.ToString()}: {reason}",
            Status = ChunkStateManager.Instance.GetChunkState(operation.ChunkCoordinate).Status,
            Timestamp = DateTime.UtcNow,
            RetryCount = operation.RetryCount
        };

        ChunkStateManager.Instance.LogChunkError(operation.ChunkCoordinate, error);
        ChunkStateManager.Instance.QuarantinedChunks.Add(operation.ChunkCoordinate);
        
        Debug.LogError($"Operation failed for chunk {operation.ChunkCoordinate}:\n" +
                    $"Operation: {operationName ?? operation.Type.ToString()}\n" +
                    $"Reason: {reason}\n" +
                    $"Current Status: {error.Status}\n" +
                    $"Retry Count: {operation.RetryCount}");
    }

    public void ProcessOperations()
    {
        // Log state only occasionally to reduce GC pressure
        if (Time.frameCount % 600 == 0) // Every ~10 seconds instead of every 5
        {
            LogQueueState();
            ClearStaleOperations();
            CleanupPendingLoadTracker();
        }

        // Calculate dynamic operations limit based on frame time
        int maxOpsThisFrame = CalculateDynamicOperationsLimit();
        int opsProcessed = 0;
        bool prioritizeInitialEmptyUnloads = World.Instance != null && World.Instance.IsInitialLoadUnloadingEmptyChunks;
        if (prioritizeInitialEmptyUnloads)
        {
            maxOpsThisFrame = int.MaxValue;
        }

        // Process critical operations first
        opsProcessed = ProcessOperationsForPriority(OperationPriority.Critical, opsProcessed, maxOpsThisFrame);
        if (opsProcessed >= maxOpsThisFrame) return;

        // Process a minimal set of unloads to free resources (max 2 per frame)
        int unloadsToProcess = prioritizeInitialEmptyUnloads
            ? Mathf.Max(1, unloadQueue.Count)
            : Mathf.Min(2, maxOpsThisFrame - opsProcessed);
        opsProcessed += ProcessUnloadOperations(unloadsToProcess);
        if (prioritizeInitialEmptyUnloads)
        {
            while (unloadQueue.Count > 0 && opsProcessed < maxOpsThisFrame)
            {
                int remainingCapacity = maxOpsThisFrame - opsProcessed;
                if (remainingCapacity <= 0)
                    break;

                int batchSize = Mathf.Clamp(unloadQueue.Count, 1, remainingCapacity);
                int before = opsProcessed;
                opsProcessed += ProcessUnloadOperations(batchSize);

                if (opsProcessed == before)
                    break;
            }
        }
        if (opsProcessed >= maxOpsThisFrame) return;

        // Process remaining operations by priority, with lower limits per priority
        var remainingOps = maxOpsThisFrame - opsProcessed;
        int highPriorityLimit = Mathf.Max(1, remainingOps / 2);
        opsProcessed += ProcessOperationsForPriority(OperationPriority.High, opsProcessed, highPriorityLimit);
        if (opsProcessed >= maxOpsThisFrame) return;

        // Split remaining capacity between normal and low priority
        remainingOps = maxOpsThisFrame - opsProcessed;
        int normalPriorityLimit = Mathf.Max(1, remainingOps / 2);
        opsProcessed += ProcessOperationsForPriority(OperationPriority.Normal, opsProcessed, normalPriorityLimit);
        if (opsProcessed >= maxOpsThisFrame) return;

        // Use any remaining capacity for low priority operations
        remainingOps = maxOpsThisFrame - opsProcessed;
        opsProcessed += ProcessOperationsForPriority(OperationPriority.Low, opsProcessed, remainingOps);
    }

    private int CalculateDynamicOperationsLimit()
    {
        // Start with the config default
        int baseLimit = MeshDataPool.Instance.GetDynamicChunksPerFrame();

        if (World.Instance != null)
        {
            if (World.Instance.IsInitialLoadUnloadingEmptyChunks)
            {
                return Mathf.Max(baseLimit, World.Instance.InitialEmptyChunksTotal);
            }

            if (World.Instance.IsInitialLoadInProgress)
            {
                return Mathf.Max(World.Instance.Config.minChunksPerFrame, World.Instance.InitialLoadChunkBudget);
            }
        }
        
        // Check memory pressure
        float memoryPressure = (float)MeshDataPool.Instance.GetCurrentMemoryUsage() / 
                            World.Instance.Config.MaxMeshCacheSize;
        
        // Get framerate information
        float frameTime = Time.unscaledDeltaTime;
        float targetFPS = Mathf.Max(10f, World.Instance.Config.chunkProcessingTargetFPS);
        float targetFrameTime = 1f / targetFPS;
        float frameTimePressure = Mathf.Clamp01(frameTime / (targetFrameTime * 2f));
        
        // Calculate the scaling factor - reduce work under pressure
        float scaleFactor = 1f - Mathf.Max(memoryPressure * 0.8f, frameTimePressure * 0.7f);
        
        // Ensure at least 1 operation and no more than config value
        return Mathf.Clamp(Mathf.RoundToInt(baseLimit * scaleFactor), World.Instance.Config.minChunksPerFrame, baseLimit);
    }

    private int ProcessOperationsForPriority(OperationPriority priority, int opsProcessed, int maxOps)
    {
        if (maxOps <= 0) return 0;
        
        int processedCount = 0;
        var queue = operationQueues[priority];
        
        // Create a temporary queue for operations we want to defer
        var deferredOps = new Queue<ChunkOperation>();
        
        while (queue.Count > 0 && processedCount < maxOps)
        {
            var operation = queue.Dequeue();
            
            // Skip if chunk is already being processed
            if (chunksInProcess.Contains(operation.ChunkCoordinate))
            {
                deferredOps.Enqueue(operation);
                continue;
            }
            
            try
            {
                chunksInProcess.Add(operation.ChunkCoordinate);
                
                // Actually process the operation
                ProcessOperation(operation);
                processedCount++;
            }
            catch (Exception e)
            {
                Debug.LogError($"Error processing {operation.Type} for chunk {operation.ChunkCoordinate}: {e.Message}");
                
                // Mark as error and add to quarantine
                LogOperationFailure(operation, e.Message);
            }
            finally
            {
                chunksInProcess.Remove(operation.ChunkCoordinate);
            }
        }
        
        // Re-enqueue deferred operations
        while (deferredOps.Count > 0)
        {
            queue.Enqueue(deferredOps.Dequeue());
        }
        
        return processedCount;
    }

    private int ProcessUnloadOperations(int maxUnloads)
    {
        if (maxUnloads <= 0) return 0;
        
        int processed = 0;
        var tempQueue = new Queue<Vector3Int>();
        
        // Process a limited number of unloads
        while (unloadQueue.Count > 0 && processed < maxUnloads)
        {
            var chunkCoord = unloadQueue.Dequeue();
            
            if (chunksInProcess.Contains(chunkCoord))
            {
                tempQueue.Enqueue(chunkCoord);
                continue;
            }
            
            try
            {
                chunksInProcess.Add(chunkCoord);
                if (ProcessUnloadOperation(chunkCoord))
                {
                    processed++;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error unloading chunk {chunkCoord}: {e.Message}");
                QuarantineChunk(chunkCoord, $"Unload error: {e.Message}");
            }
            finally
            {
                chunksInProcess.Remove(chunkCoord);
            }
        }
        
        // Re-enqueue chunks we couldn't process
        while (tempQueue.Count > 0)
        {
            unloadQueue.Enqueue(tempQueue.Dequeue());
        }
        
        return processed;
    }
    
    private bool HasPendingOperationsOfPriority(OperationPriority priority)
    {
        return operationQueues[priority].Count > 0;
    }

    private void ProcessOperation(ChunkOperation operation)
    {
        if (!ValidateOperationState(operation))
        {
            Debug.LogWarning($"[ChunkQueue] Operation validation failed for {operation.Type} on chunk {operation.ChunkCoordinate}");
            return;
        }

        try
        {
            bool success = false;
            switch (operation.Type)
            {
                case OperationType.Load:
                    success = ProcessLoadOperation(operation.ChunkCoordinate, operation.QuickCheck);
                    // Remove from pending loads set regardless of success
                    chunksWithPendingLoads.Remove(operation.ChunkCoordinate);
                    break;
                case OperationType.Unload:
                    success = ProcessUnloadOperation(operation.ChunkCoordinate);
                    break;
                case OperationType.Modify:
                    ProcessModifyOperation(operation);
                    success = true;
                    break;
                case OperationType.Save:
                    ProcessSaveOperation(operation);
                    success = true;
                    break;
                case OperationType.Generate:
                    ProcessGenerateOperation(operation);
                    success = true;
                    break;
                default:
                    Debug.LogError($"[ChunkQueue] Unknown operation type: {operation.Type}");
                    success = false;
                    break;
            }

            if (!success)
            {
                World.Instance.QuarantineChunk(operation.ChunkCoordinate, 
                    $"Operation {operation.Type} failed to complete",
                    operation.RequiredInitialStatus);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[ChunkQueue] Error processing {operation.Type} for chunk {operation.ChunkCoordinate}: {e}");
            chunksWithPendingLoads.Remove(operation.ChunkCoordinate); // Clean up in case of error
            World.Instance.QuarantineChunk(operation.ChunkCoordinate, 
                $"Exception during {operation.Type}: {e.Message}",
                operation.RequiredInitialStatus);
        }
    }

    private void CleanupPendingLoadTracker()
    {
        if (Time.frameCount % 300 == 0) // Every ~5 seconds
        {
            var chunksToRemove = new List<Vector3Int>();
            
            foreach (var chunkCoord in chunksWithPendingLoads)
            {
                bool hasLoadOp = false;
                foreach (var queue in operationQueues.Values)
                {
                    if (queue.Any(op => op.ChunkCoordinate == chunkCoord && op.Type == OperationType.Load))
                    {
                        hasLoadOp = true;
                        break;
                    }
                }
                
                if (!hasLoadOp)
                {
                    chunksToRemove.Add(chunkCoord);
                }
            }

            foreach (var chunk in chunksToRemove)
            {
                chunksWithPendingLoads.Remove(chunk);
            }
        }
    }

    private bool ProcessLoadOperation(Vector3Int chunkCoord, bool quickCheck = true)
    {
        try
        {
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);

            // 1) Abort if chunk already loaded
            if (World.Instance.TryGetChunk(chunkCoord, out _))
            {
                return false;
            }

            // Validate state transition
            if (!ChunkStateManager.Instance.TryChangeState(
                chunkCoord,
                ChunkConfigurations.ChunkStatus.Loading,
                ChunkConfigurations.ChunkStateFlags.None))
            {
                Debug.LogWarning($"[ChunkQueue] Failed to change state to Loading for chunk {chunkCoord}");
                return false;
            }

            // 2) Get a chunk from pool
            Chunk chunkObject = ChunkPoolManager.Instance.GetChunk();
            if (chunkObject == null)
            {
                Debug.LogError($"Failed to get chunk from pool for {chunkCoord}");
                return false;
            }

            // 3) Position and initialize
            Vector3 chunkPosition = Coord.GetWorldPosition(
                chunkCoord,
                Vector3Int.zero,
                World.Instance.chunkSize,
                World.Instance.voxelSize);

            chunkObject.transform.position = chunkPosition;
            chunkObject.gameObject.SetActive(true);
            chunkObject.Initialize(
                World.Instance.chunkSize,
                World.Instance.surfaceLevel,
                World.Instance.voxelSize,
                chunkPosition,
                quickCheck); // Pass the quickCheck parameter

            // 4) Register with the World
            World.Instance.RegisterChunk(chunkCoord, chunkObject);

            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load chunk {chunkCoord}: {e}");
            return false;
        }
    }

    private bool ProcessUnloadOperation(Vector3Int chunkCoord)
    {
        try
        {
            if (!World.Instance.TryGetChunk(chunkCoord, out Chunk chunk))
            {
                if (World.Instance.NotifyInitialEmptyChunkUnloaded(chunkCoord))
                {
                    unloadingChunks.Remove(chunkCoord);
                    return true;
                }

                Debug.LogWarning($"[ChunkQueue] Cannot unload chunk {chunkCoord} - not found");
                return false;
            }

            // 1) Complete any pending jobs
            chunk.CompleteAllJobs();

            // 2) Mark chunk as "Unloading" so other ops won't collide
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            
            if (!ChunkStateManager.Instance.TryChangeState(
                chunkCoord,
                ChunkConfigurations.ChunkStatus.Unloading,
                ChunkConfigurations.ChunkStateFlags.None))
            {
                Debug.LogWarning($"[ChunkQueue] Failed to change state to Unloading for chunk {chunkCoord}");
                return false;
            }

            // 3) If chunk data was modified, save it
            var chunkData = chunk.GetChunkData();
            if (chunkData != null && chunkData.HasModifiedData)
            {
                chunkData.SaveData();
            }

            // 4) Return to the pool
            ChunkPoolManager.Instance.ReturnChunk(chunk);

            // 5) Remove the chunk from the World dictionary
            World.Instance.RemoveChunk(chunkCoord);

            // 6) Mark chunk as "Unloaded"
            if (ChunkStateManager.Instance.TryChangeState(
                chunkCoord,
                ChunkConfigurations.ChunkStatus.Unloaded,
                ChunkConfigurations.ChunkStateFlags.None))
            {
                World.Instance.NotifyInitialEmptyChunkUnloaded(chunkCoord);
                unloadingChunks.Remove(chunkCoord);
                return true;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error unloading chunk {chunkCoord}: {e}");
            World.Instance.QuarantineChunk(
                chunkCoord,
                $"Unload error: {e.Message}",
                ChunkConfigurations.ChunkStatus.Unloading
            );
        }

        return false;
    }

    private void ProcessModifyOperation(ChunkOperation operation)
    {
        if (!World.Instance.TryGetChunk(operation.ChunkCoordinate, out Chunk chunk))
        {
            QuarantineChunk(operation.ChunkCoordinate, "Chunk not found for modify operation");
            return;
        }

        chunk.CompleteAllJobs();

        if (operation.Parameters is Action<Chunk> modifyAction)
        {
            modifyAction(chunk);
        }

        ChunkStateManager.Instance.TryChangeState(
            operation.ChunkCoordinate,
            ChunkConfigurations.ChunkStatus.Modified,
            ChunkConfigurations.ChunkStateFlags.Active);

        chunk.Generate(false, false);
        OperationComplete(operation.ChunkCoordinate);
    }

    private void ProcessSaveOperation(ChunkOperation operation)
    {
        if (!World.Instance.TryGetChunk(operation.ChunkCoordinate, out Chunk chunk))
        {
            QuarantineChunk(operation.ChunkCoordinate, "Chunk not found for save operation");
            return;
        }

        chunk.CompleteAllJobs();
        chunk.GetChunkData().SaveData();

        ChunkStateManager.Instance.TryChangeState(
            operation.ChunkCoordinate,
            ChunkConfigurations.ChunkStatus.Saved,
            ChunkConfigurations.ChunkStateFlags.Active);

        OperationComplete(operation.ChunkCoordinate);
    }

    private void ProcessGenerateOperation(ChunkOperation operation)
    {
        if (!World.Instance.TryGetChunk(operation.ChunkCoordinate, out Chunk chunk))
        {
            QuarantineChunk(operation.ChunkCoordinate, "Chunk not found for generate operation");
            return;
        }

        chunk.Generate(false, true);
        OperationComplete(operation.ChunkCoordinate);
    }

    private bool ValidateOperationState(ChunkOperation operation)
    {
        var state = ChunkStateManager.Instance.GetChunkState(operation.ChunkCoordinate);
        
        // For load operations
        if (operation.Type == OperationType.Load)
        {
            // Allow loading if either:
            // 1. No chunk exists and state is None
            // 2. No chunk exists and state is Unloaded
            bool chunkExists = World.Instance.TryGetChunk(operation.ChunkCoordinate, out _);
            bool validState = state.Status == ChunkConfigurations.ChunkStatus.None || 
                            state.Status == ChunkConfigurations.ChunkStatus.Unloaded;
            bool canLoad = !chunkExists && validState;
            
            if (!canLoad)
            {
                if (chunkExists)
                {
                    Debug.LogWarning($"[ChunkQueue] Cannot load chunk {operation.ChunkCoordinate} - Chunk already exists");
                }
                else
                {
                    Debug.LogWarning($"[ChunkQueue] Cannot load chunk {operation.ChunkCoordinate} - Invalid state for loading: {state.Status}");
                }
            }
            return canLoad;
        }

        // For unload operations, make sure the chunk exists first
        if (operation.Type == OperationType.Unload)
        {
            bool exists = World.Instance.TryGetChunk(operation.ChunkCoordinate, out _);
            if (!exists)
            {
                Debug.LogWarning($"[ChunkQueue] Cannot unload chunk {operation.ChunkCoordinate} - Chunk doesn't exist");
            }
            return exists;
        }
        
        // For other operation types, ensure we have the chunk and it's in a valid state
        bool hasChunk = World.Instance.TryGetChunk(operation.ChunkCoordinate, out _);
        bool stateValid = (state.Status == operation.RequiredInitialStatus) || 
                        (operation.RequiredInitialStatus == ChunkConfigurations.ChunkStatus.None);
        
        if (!hasChunk)
        {
            Debug.LogWarning($"[ChunkQueue] Cannot perform {operation.Type} on chunk {operation.ChunkCoordinate} - Chunk not found");
            return false;
        }
        
        if (!stateValid)
        {
            Debug.LogWarning($"[ChunkQueue] Cannot perform {operation.Type} on chunk {operation.ChunkCoordinate} - Invalid state: {state.Status}, expected: {operation.RequiredInitialStatus}");
            return false;
        }
        
        return true;
    }

    private void QuarantineChunk(Vector3Int chunkCoord, string reason)
    {
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
        World.Instance.QuarantineChunk(chunkCoord, reason, state.Status);
    }

    private void EnqueueOperation(ChunkOperation operation)
    {
        if (chunksInProcess.Contains(operation.ChunkCoordinate))
        {
            Debug.LogWarning($"Chunk {operation.ChunkCoordinate} already being processed, deferring operation");
            return;
        }

        // Count existing operations of the same type
        int existingOps = 0;
        foreach (var queue in operationQueues.Values)
        {
            existingOps += queue.Count(op => 
                op.ChunkCoordinate == operation.ChunkCoordinate && 
                op.Type == operation.Type);
        }

        // Only warn and skip if same operation type exists
        if (existingOps > 0)
        {
            // For Load/Unload operations, we want to allow only one
            if (operation.Type == OperationType.Load || operation.Type == OperationType.Unload)
            {
                return;
            }
            
            // For other operations, allow queueing but log for debugging
            if (existingOps > World.Instance.Config.MaxJobRetries)
            {
                Debug.LogWarning($"Chunk {operation.ChunkCoordinate} has exceeded max operations of type {operation.Type}");
                return;
            }
        }

        // Track load operations in the HashSet
        if (operation.Type == OperationType.Load)
        {
            chunksWithPendingLoads.Add(operation.ChunkCoordinate);
        }

        operationQueues[operation.Priority].Enqueue(operation);
    }

    private void ClearStaleOperations()
    {
        DateTime currentTime = DateTime.UtcNow;
        int clearedCount = 0;
        
        foreach (var priority in operationQueues.Keys.ToList())
        {
            var tempQueue = new Queue<ChunkOperation>();
            while (operationQueues[priority].Count > 0)
            {
                var op = operationQueues[priority].Dequeue();
                
                // Only check operations that have had errors
                if (op.RetryCount > 0)
                {
                    var state = ChunkStateManager.Instance.GetChunkState(op.ChunkCoordinate);
                        
                    // Move to quarantine if we've had actual errors and exceeded retry attempts
                    if (op.RetryCount >= World.Instance.Config.MaxJobRetries)
                    {
                        World.Instance.QuarantineChunk(op.ChunkCoordinate, 
                            $"Operation {op.Type} failed after {op.RetryCount} retries", 
                            state.Status);
                        clearedCount++;
                        continue;
                    }
                }
                
                tempQueue.Enqueue(op);
            }
            
            operationQueues[priority] = tempQueue;
        }

        if (clearedCount > 0)
        {
            Debug.LogWarning($"Cleared {clearedCount} failed operations from queue");
        }
    }

    private void LogQueueState()
    {
        int totalQueued = 0;
        string queueDetails = "";
        foreach (var priority in operationQueues.Keys)
        {
            int count = operationQueues[priority].Count;
            totalQueued += count;
            queueDetails += $"\n{priority}: {count}";
        }
        
        Debug.Log($"Queue State: Total={totalQueued}, " +
                $"Loading={loadingChunks.Count}, " +
                $"Unloading={unloadingChunks.Count}, " +
                $"InProcess={chunksInProcess.Count}" +
                $"{queueDetails}");
    }

    public void OperationComplete(Vector3Int chunkCoord)
    {
        chunksInProcess.Remove(chunkCoord);
        activeOperations.Remove(chunkCoord);
    }
}