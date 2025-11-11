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

        public PendingDensityPointUpdate(Vector3Int pointPosition, float newDensity)
        {
            this.pointPosition = pointPosition;
            this.newDensity = newDensity;
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
    private const float VALIDATION_CACHE_TIME = 0.1f; // Cache results for 100ms

    private HashSet<Vector3Int> modifiedSolidChunks = new HashSet<Vector3Int>();
    private Dictionary<ulong, Vector3> activePlayerPositions = new Dictionary<ulong, Vector3>();
    private Dictionary<ulong, Vector3Int> playerChunkCoordinates = new Dictionary<ulong, Vector3Int>();
    private HashSet<Vector3Int> activeChunkCoords = new HashSet<Vector3Int>();
    private float lastGlobalChunkUpdateTime = 0f;
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
        TerrainAnalysisCache.Update();
        CleanupValidationCache();
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
    }

    public bool IsSolidChunkMarkedForModification(Vector3Int chunkCoord)
    {
        return modifiedSolidChunks.Contains(chunkCoord);
    }

    public bool RemoveChunk(Vector3Int coordinate)
    {
        return chunks.Remove(coordinate);
    }

    public bool IsChunkLoaded(Vector3Int chunkCoordinates)
    {
        return chunks.ContainsKey(chunkCoordinates);
    }

    public void ResetTerrainAnalysisCache()
    {
        // Reset any in-memory cache state when loading a new world
        TerrainAnalysisCache.Update();
        
        // This forces the TerrainAnalysisCache to load data specific to the current world
        for (int x = -1; x <= 1; x++)
        for (int z = -1; z <= 1; z++)
        {
            Vector3Int sampleCoord = lastPlayerChunkCoordinates + new Vector3Int(x, 0, z);
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
        
        if (IsInitialLoadInProgress)
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
            bool immediate = GetMinDistanceToPlayers(coord) <= 2;
            operationsQueue.QueueChunkForLoad(coord, immediate);
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
            
        // Check for age-based forcing
        float timeSinceAccess = Time.time - chunk.lastAccessTime;
        
        // Force unload for chunks that haven't been accessed in over 5 minutes
        // regardless of other conditions, as long as no player is extremely close
        bool shouldForceUnload = timeSinceAccess > 300f; // 5 minutes
        
        if (shouldForceUnload)
        {
            bool playerVeryClose = false;
            
            foreach (var playerEntry in playerChunkCoordinates)
            {
                Vector3Int playerChunk = playerEntry.Value;
                float distance = Vector3Int.Distance(chunkCoord, playerChunk);
                if (distance <= 3) // Within 3 chunks
                {
                    playerVeryClose = true;
                    break;
                }
            }
            
            // Force unload old chunks except when players are very close
            if (!playerVeryClose)
            {
                Debug.Log($"Forcing unload of chunk {chunkCoord} - not accessed for {timeSinceAccess:F0} seconds");
                return true;
            }
        }
        
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
            
            // CRITICAL FIX: Always invalidate terrain analysis for ALL affected chunks
            foreach (var neighborCoord in affectedChunks)
            {
                // Always invalidate the analysis - this ensures we don't skip loading modified chunks
                TerrainAnalysisCache.InvalidateAnalysis(neighborCoord);
                
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
                    // If we couldn't load the chunk, queue updates for later
                    QueueDensityUpdate(neighborCoord, worldPos);
                    
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
                    
                    bool densityChanged = ApplyDensityUpdate(neighborChunk, worldPos, false);
                    
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
        
        // Skip if quarantined
        if (ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord))
        {
            Debug.LogWarning($"Can't load quarantined chunk {chunkCoord}");
            return false;
        }

        try
        {
            // Get current state first
            var currentState = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            
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

            // Get a chunk from pool
            Chunk chunkObject = ChunkPoolManager.Instance.GetChunk();
            if (chunkObject == null)
            {
                Debug.LogError($"Failed to get chunk from pool for {chunkCoord}");
                return false;
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

    private bool ApplyDensityUpdate(Chunk chunk, Vector3 worldPos, bool isAdding)
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
        
        // Calculate influencing radius based on voxel size
        float radius = voxelSize * Config.densityInfluenceRadius;
        
        // For solid chunks, use a larger radius to ensure we modify enough of the chunk
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
        Debug.Log($"Mining world pos: {worldPos}, local density pos in chunk {chunkCoord}: {localMiningPos}");
        
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
            minX = Mathf.Max(0, localMiningPos.x - densityRange);
            maxX = Mathf.Min(totalPointsPerAxis - 1, localMiningPos.x + densityRange);
            minY = Mathf.Max(0, localMiningPos.y - densityRange);
            maxY = Mathf.Min(totalPointsPerAxis - 1, localMiningPos.y + densityRange);
            minZ = Mathf.Max(0, localMiningPos.z - densityRange);
            maxZ = Mathf.Min(totalPointsPerAxis - 1, localMiningPos.z + densityRange);
        }
        
        // Safely iterate through the region and update density values
        for (int x = minX; x <= maxX; x++)
        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        {
            Vector3Int densityPos = new Vector3Int(x, y, z);
            
            // Skip invalid positions
            if (!Coord.IsDensityPositionValid(densityPos, totalPointsPerAxis))
            {
                continue;
            }
            
            // Calculate world position of this density point
            Vector3 pointWorldPos = chunkOrigin + new Vector3(x, y, z) * voxelSize;
            
            // Calculate distance from the mining point for falloff
            float distance = Vector3.Distance(worldPos, pointWorldPos);
            float falloff = CalculateDensityFalloff(distance, radius);
            
            // Only apply significant changes
            if (falloff > Config.minDensityChangeThreshold)
            {
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
                        bool success = chunk.TrySetDensityPoint(densityPos, newDensity);
                        if (success)
                        {
                            anyDensityChanged = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error updating density at {densityPos} in chunk {chunkCoord}: {e.Message}");
                }
            }
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
            bool isSolidChunk = TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis.IsSolid;
            
            // Use increased margin for solid chunks
            if (isSolidChunk)
            {
                chunkMargin *= 1.5f;
            }
            
            // Calculate distance from mining point to nearest chunk face instead of center
            float distanceToChunk = DistanceToChunkBounds(worldPos, chunkCoord);
            
            // If we're within range of the chunk, add it
            if (distanceToChunk <= radius + voxelSize)
            {
                affectedChunks.Add(chunkCoord);
                
                // If this chunk is solid, make sure we mark it for modification
                if (isSolidChunk)
                {
                    MarkSolidChunkForModification(chunkCoord);
                }
            }
            // Also include solid chunks that are just a bit further away
            else if (isSolidChunk && distanceToChunk <= radius * 1.5f)
            {
                Debug.Log($"Including nearby solid chunk {chunkCoord} for potential modification");
                affectedChunks.Add(chunkCoord);
                MarkSolidChunkForModification(chunkCoord);
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
            if (!pendingDensityPointUpdates.ContainsKey(chunkCoord))
            {
                pendingDensityPointUpdates[chunkCoord] = new List<PendingDensityPointUpdate>();
            }

            // Convert world position to this chunk's local coordinate system
            Vector3 chunkOrigin = GetChunkWorldPosition(chunkCoord);
            Vector3Int localPos = Coord.WorldToVoxelCoord(worldPos, chunkOrigin, voxelSize);
            
            // Now convert to density position
            Vector3Int densityPos = Coord.VoxelToDensityCoord(localPos);
            
            // Verify the position is valid
            if (!Coord.IsDensityPositionValid(densityPos, chunkSize + 1))
            {
                Debug.LogWarning($"Ignoring invalid density position {densityPos} for chunk {chunkCoord}");
                return;
            }

            // Calculate new density value (higher value = outside surface)
            float targetDensity = surfaceLevel + 1.5f; // Ensure we create a hole
            
            // Handle solid chunks specially
            bool isSolidChunk = false;
            if (TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis.IsSolid)
            {
                isSolidChunk = true;
                // For solid chunks, use a higher value to ensure proper modification
                targetDensity = surfaceLevel + 2.0f;
                Debug.Log($"Queuing special solid chunk update for {chunkCoord} at {densityPos} with density {targetDensity}");
            }
            
            pendingDensityPointUpdates[chunkCoord].Add(
                new PendingDensityPointUpdate(densityPos, targetDensity));
            
            // CRITICAL FIX: If this is a solid chunk, mark it for modification immediately
            if (isSolidChunk)
            {
                // Mark for modification and update terrain analysis to reflect this
                MarkSolidChunkForModification(chunkCoord);
            }
            
            // Only request load if not already loaded
            if (!chunks.ContainsKey(chunkCoord))
            {
                var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
                if (state.Status != ChunkConfigurations.ChunkStatus.Loading)
                {
                    if (isSolidChunk)
                    {
                        // Use the direct loading method for solid chunks
                        LoadChunkImmediately(chunkCoord);
                    }
                    else
                    {
                        RequestImmediateChunkLoad(chunkCoord);
                    }
                }
            }
        }
    }

    public void RequestChunkLoad(Vector3Int chunkCoord)
    {
        // Skip if chunk is quarantined
        if (ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord))
        {
            Debug.LogWarning($"Skipping load request for quarantined chunk {chunkCoord}");
            return;
        }

        // Skip if chunk is already loaded or in queue
        if (!chunks.ContainsKey(chunkCoord))
        {
            bool hasPendingUpdates = pendingVoxelUpdates.ContainsKey(chunkCoord) || 
                                    pendingDensityPointUpdates.ContainsKey(chunkCoord);
                                    
            try
            {
                operationsQueue.QueueChunkForLoad(chunkCoord, hasPendingUpdates);
            }
            catch (Exception e)
            {
                OnChunkLoadFailed(chunkCoord, e.Message);
            }
        }
    }
    
    private void ProcessPendingUpdates()
    {
        if (Time.time - lastUpdateTime < Config.updateInterval) return;
        lastUpdateTime = Time.time;
        
        int updatesProcessed = 0;
        int maxUpdatesThisFrame = MeshDataPool.Instance.GetDynamicChunksPerFrame();
        
        // To track chunks that had density modifications
        HashSet<Vector3Int> modifiedChunks = new HashSet<Vector3Int>();
            
        lock (updateLock)
        {
            // Get a snapshot of the keys we need to process
            Vector3Int[] voxelKeys;
            Vector3Int[] densityKeys;
            
            try
            {
                voxelKeys = pendingVoxelUpdates.Keys.ToArray();
                densityKeys = pendingDensityPointUpdates.Keys.ToArray();
            }
            catch (Exception e)
            {
                Debug.LogError($"Error creating key snapshots: {e.Message}");
                return;
            }

            // Process voxel updates using the snapshot
            foreach (var chunkCoord in voxelKeys)
            {
                if (updatesProcessed >= maxUpdatesThisFrame) break;
                
                if (pendingVoxelUpdates.ContainsKey(chunkCoord))  // Check if still exists
                {
                    if (chunks.TryGetValue(chunkCoord, out Chunk chunk))
                    {
                        var updates = new List<PendingVoxelUpdate>(pendingVoxelUpdates[chunkCoord]);
                        pendingVoxelUpdates.Remove(chunkCoord);  // Remove immediately after copying

                        foreach (var update in updates)
                        {
                            if (update.isAdding)
                                chunk.AddVoxel(update.voxelPosition);
                            else
                                chunk.DamageVoxel(update.voxelPosition, 1);
                        }
                        modifiedChunks.Add(chunkCoord);
                        updatesProcessed++;
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
            }

            // Only process density updates if we haven't hit our limit
            if (updatesProcessed < maxUpdatesThisFrame)
            {
                // Process density updates using the snapshot
                foreach (var chunkCoord in densityKeys)
                {
                    if (updatesProcessed >= maxUpdatesThisFrame) break;
                    
                    if (pendingDensityPointUpdates.ContainsKey(chunkCoord))  // Check if still exists
                    {
                        bool isSolidChunk = TerrainAnalysisCache.TryGetAnalysis(chunkCoord, out var analysis) && analysis.IsSolid;
                        
                        if (chunks.TryGetValue(chunkCoord, out Chunk chunk))
                        {
                            var updates = new List<PendingDensityPointUpdate>(pendingDensityPointUpdates[chunkCoord]);
                            pendingDensityPointUpdates.Remove(chunkCoord);  // Remove immediately after copying

                            bool chunkModified = false;
                            foreach (var update in updates)
                            {
                                chunk.SetDensityPoint(update.pointPosition, update.newDensity);
                                chunkModified = true;
                            }
                            
                            if (chunkModified)
                            {
                                modifiedChunks.Add(chunkCoord);
                                
                                // CRITICAL FIX: If this was a solid chunk, properly mark as modified
                                if (isSolidChunk)
                                {
                                    // Update terrain analysis immediately
                                    TerrainAnalysisCache.SaveAnalysis(chunkCoord, false, false, true);
                                    ChunkStateManager.Instance.TryChangeState(
                                        chunkCoord,
                                        ChunkConfigurations.ChunkStatus.Modified,
                                        ChunkConfigurations.ChunkStateFlags.Active
                                    );
                                }
                            }
                            updatesProcessed++;
                        }
                        else
                        {
                            // CRITICAL FIX: For solid chunks that don't exist yet, keep trying
                            if (isSolidChunk)
                            {
                                // Ensure solid chunks are properly marked
                                MarkSolidChunkForModification(chunkCoord);
                                // Try to force load the chunk
                                LoadChunkImmediately(chunkCoord);
                                // Keep updates for next frame
                            }
                            else
                            {
                                pendingDensityPointUpdates.Remove(chunkCoord);  // Remove if chunk doesn't exist
                            }
                        }
                    }
                }
            }

            // CRITICAL FIX: Always update and save modified chunks
            foreach (var chunkCoord in modifiedChunks)
            {
                if (chunks.TryGetValue(chunkCoord, out Chunk chunk))
                {
                    // Queue mesh update
                    chunk.isMeshUpdateQueued = true;
                    chunksNeedingMeshUpdate.Add(chunk);
                    
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
                    var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
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
        if (chunksNeedingMeshUpdate.Count == 0) return;

        // Process mesh updates in batches
        int maxUpdatesPerFrame = MeshDataPool.Instance.GetDynamicChunksPerFrame();
        int updatesThisFrame = 0;
        var processedChunks = new List<Chunk>();

        foreach (var chunk in chunksNeedingMeshUpdate)
        {
            if (updatesThisFrame >= maxUpdatesPerFrame) break;

            Vector3Int chunkCoord = Coord.WorldToChunkCoord(chunk.transform.position, chunkSize, voxelSize);
            if (!chunk.gameObject.activeInHierarchy || chunk.generationCoroutine != null)
                continue;

            chunk.Generate(log: false, fullMesh: false);
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
        else if (state.Status == ChunkConfigurations.ChunkStatus.Loaded)
        {
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
                            chunksWithPendingNeighborUpdates.Contains(chunkCoord);
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

    private void UpdateChunks(Vector3Int centerChunkCoordinates)
    {
        if (ShouldLoadChunk(centerChunkCoordinates))
        {
            // CHANGE THIS LINE - Enable quickCheck for center chunk
            operationsQueue.QueueChunkForLoad(centerChunkCoordinates, true, true);
            
            // Also load immediate neighbors
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
            {
                Vector3Int neighborCoord = centerChunkCoordinates + new Vector3Int(x, 0, z);
                if (ShouldLoadChunk(neighborCoord))
                {
                    // CHANGE THIS LINE - Enable quickCheck for neighbors
                    operationsQueue.QueueChunkForLoad(neighborCoord, true, true);
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
            bool immediate = request.Distance <= 2; // Immediate load for very close chunks
            
            // CHANGE THIS LINE - Make sure quickCheck is true
            operationsQueue.QueueChunkForLoad(request.Coordinate, immediate, true);
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
            
            // Skip if quarantined
            if (ChunkStateManager.Instance.QuarantinedChunks.Contains(chunkCoord))
            {
                loadValidationCache[chunkCoord] = Time.time;
                return false;
            }
            
            // Skip if already loaded
            if (chunks.ContainsKey(chunkCoord))
            {
                return false;
            }

            // Get current chunk state
            var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
            
            // 2. Check for conditions that should ALWAYS force a load
            
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

            // Chunks with pending updates should always load
            if (HasPendingUpdates(chunkCoord))
            {
                if (ChunkLifecycleLogsEnabled)
                {
                    Debug.Log($"[ShouldLoadChunk] Forcing load of chunk {chunkCoord} with pending updates");
                }
                loadValidationCache[chunkCoord] = Time.time;
                return true;
            }

            // 3. Check basic state requirements
            
            // Allow loading if chunk is None or Unloaded
            bool validState = (state.Status == ChunkConfigurations.ChunkStatus.None || 
                            state.Status == ChunkConfigurations.ChunkStatus.Unloaded);
            
            if (!validState)
            {
                if (ChunkLifecycleLogsEnabled)
                {
                    Debug.Log($"[ShouldLoadChunk] Chunk {chunkCoord} in invalid state for loading: {state.Status}");
                }
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
                    if (ChunkLifecycleLogsEnabled)
                    {
                        Debug.Log($"[ShouldLoadChunk] Forcing load of chunk {chunkCoord} marked as modified in analysis cache");
                    }
                    loadValidationCache[chunkCoord] = Time.time;
                    return true;
                }
                
                // Skip empty or solid chunks ONLY if they don't have pending updates
                // and aren't marked for modification
                if ((analysis.IsEmpty || analysis.IsSolid) && 
                    !modifiedSolidChunks.Contains(chunkCoord) && 
                    !HasPendingUpdates(chunkCoord))
                {
                    if (ChunkLifecycleLogsEnabled)
                    {
                        Debug.Log($"[ShouldLoadChunk] Skipping load of {(analysis.IsEmpty ? "empty" : "solid")} chunk {chunkCoord}");
                    }
                    loadValidationCache[chunkCoord] = Time.time;
                    return false;
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
        
        // Also invalidate the terrain analysis for this chunk
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
        
        // If chunk exists but is in wrong state, force unload first
        if (chunks.ContainsKey(chunkCoord))
        {
            Debug.LogWarning($"[ImmediateLoad] Requesting unload for {chunkCoord}, wrong state for ImmediateLoad");
            operationsQueue.QueueChunkForUnload(chunkCoord);
            return; // The unload operation will trigger a reload when complete
        }

        // Only queue if in valid state for loading
        if (state.Status == ChunkConfigurations.ChunkStatus.None ||
            state.Status == ChunkConfigurations.ChunkStatus.Unloaded)
        {
            operationsQueue.QueueChunkForLoad(chunkCoord, immediate: true, quickCheck: false);
        }
        else
        {
            Debug.LogWarning($"[World] Cannot load chunk {chunkCoord} in state {state.Status}");
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

        // Clear any pending operations
        if (chunks.TryGetValue(chunkCoord, out Chunk chunk))
        {
            chunk.CompleteAllJobs();
        }

        ClearPendingUpdates(chunkCoord);
        
        Debug.LogWarning($"Chunk {chunkCoord} quarantined. Reason: {reason}, Last Status: {lastStatus}");
    }

    private bool AttemptChunkRecovery(Vector3Int chunkCoord)
    {
        var state = ChunkStateManager.Instance.GetChunkState(chunkCoord);
        if (state.Status == ChunkConfigurations.ChunkStatus.Error)
        {
            // Reset chunk state
            if (ChunkStateManager.Instance.TryChangeState(
                chunkCoord, 
                ChunkConfigurations.ChunkStatus.None,
                ChunkConfigurations.ChunkStateFlags.None))
            {
                // Request new load
                operationsQueue.QueueChunkForLoad(chunkCoord, true);
                return true;
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

    public Vector3 GetChunkWorldPosition(Vector3Int chunkCoord)
    {
        return new Vector3(
            chunkCoord.x * chunkSize * voxelSize,
            chunkCoord.y * chunkSize * voxelSize,
            chunkCoord.z * chunkSize * voxelSize
        );
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