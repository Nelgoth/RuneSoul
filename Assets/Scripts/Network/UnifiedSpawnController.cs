using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using ControllerAssets;
using UnityEngine.InputSystem;
using NelsUtils;

/// <summary>
/// Unified spawn controller that manages the entire player spawn process from connection to activation.
/// This replaces the scattered logic in PlayerSpawner, PlayerSpawnSafety, and NetworkPlayerSetup.
/// </summary>
public class UnifiedSpawnController : MonoBehaviour
{
    #region Serialized Fields
    [Header("Spawn Settings")]
    [SerializeField] private Vector3 defaultSpawnPosition = Vector3.zero;
    [SerializeField] private float minHeightAboveGround = 2f;
    [SerializeField] private LayerMask terrainLayer;
    
    [Header("Vertical Search")]
    [SerializeField] private float[] verticalSearchHeights = { 0, 50, 100, 150, 200, -20, -50, -100 };
    [SerializeField] private float maxRaycastDistance = 200f;
    [SerializeField] private int spiralSearchRings = 5;
    [SerializeField] private float spiralSearchSpacing = 10f;
    
    [Header("Chunk Loading")]
    [SerializeField] private int spawnChunkRadius = 2; // 5x5x5 area
    [SerializeField] private int centerChunkRadius = 1; // 3x3x3 center that must load
    [SerializeField] private float chunkLoadTimeout = 10f; // Increased to ensure collision is ready
    [SerializeField] private float chunkLoadCheckInterval = 0.1f;
    [SerializeField] private float postLoadWaitTime = 1f; // Wait after chunks load for collision to be ready
    
    [Header("Timing")]
    [SerializeField] private float worldInitTimeout = 10f;
    [SerializeField] private float gravitySuspensionDuration = 10f;
    [SerializeField] private int physicsSettleFrames = 2;
    
    [Header("Debugging")]
    [SerializeField] private bool showDebugLogs = true;
    #endregion

    #region State Machine
    public enum PlayerSpawnState
    {
        NotSpawned,           // Player hasn't connected yet
        WaitingForWorld,      // Waiting for WorldSaveManager init
        DeterminingPosition,  // Running vertical search algorithm
        RegisteringWithWorld, // Calling UpdatePlayerPosition
        LoadingTerrain,       // Waiting for chunks to load
        SettingPosition,      // Teleporting player
        ActivatingComponents, // Enabling controls
        Active,               // Fully operational
        Failed                // Spawn failed, needs manual intervention
    }
    
    private Dictionary<ulong, PlayerSpawnState> playerStates = new Dictionary<ulong, PlayerSpawnState>();
    private Dictionary<ulong, GameObject> playerObjects = new Dictionary<ulong, GameObject>();
    private Dictionary<ulong, Coroutine> spawnCoroutines = new Dictionary<ulong, Coroutine>();
    #endregion

    #region Singleton
    private static UnifiedSpawnController instance;
    public static UnifiedSpawnController Instance => instance;
    
    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
        
        // Set terrain layer if not assigned
        if (terrainLayer == 0)
        {
            terrainLayer = LayerMask.GetMask("Terrain");
        }
    }
    #endregion

    #region Initialization
    private void Start()
    {
        // Register with NetworkManager if available
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.ConnectionApprovalCallback += HandleConnectionApproval;
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            
            DebugLog("Registered with NetworkManager for connection callbacks");
        }
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.ConnectionApprovalCallback -= HandleConnectionApproval;
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }
    #endregion

    #region Phase 0: Connection Approval
    private void HandleConnectionApproval(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
        DebugLog($"[Phase 0] Connection approval for client {request.ClientNetworkId}");
        
        // Always approve
        response.Approved = true;
        response.CreatePlayerObject = true;
        
        // Use placeholder position - DON'T determine real position yet
        // WorldSaveManager might not be ready
        response.Position = Vector3.zero;
        response.Rotation = Quaternion.identity;
        
        // Initialize player state
        playerStates[request.ClientNetworkId] = PlayerSpawnState.NotSpawned;
        
        DebugLog($"[Phase 0] Client {request.ClientNetworkId} approved with placeholder position");
    }
    #endregion

    #region Phase Orchestration
    private void OnClientConnected(ulong clientId)
    {
        if (!NetworkManager.Singleton.IsServer) return;
        
        DebugLog($"Client {clientId} connected, starting spawn process");
        
        // Start the spawn process
        playerStates[clientId] = PlayerSpawnState.WaitingForWorld;
        
        // Find player object and start positioning
        StartCoroutine(FindPlayerObjectAndPosition(clientId));
    }

    private void OnClientDisconnected(ulong clientId)
    {
        DebugLog($"Client {clientId} disconnected, cleaning up");
        
        // Stop any running spawn coroutine
        if (spawnCoroutines.TryGetValue(clientId, out Coroutine coroutine))
        {
            if (coroutine != null)
                StopCoroutine(coroutine);
            spawnCoroutines.Remove(clientId);
        }
        
        // Clean up state
        playerStates.Remove(clientId);
        playerObjects.Remove(clientId);
    }

    private IEnumerator FindPlayerObjectAndPosition(ulong clientId)
    {
        DebugLog($"[FindPlayerObject] Searching for player object for client {clientId}");
        
        // Wait for player object to spawn
        GameObject playerObj = null;
        float timeout = 5f;
        float elapsed = 0f;
        
        while (playerObj == null && elapsed < timeout)
        {
            playerObj = FindPlayerObject(clientId);
            if (playerObj == null)
            {
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }
        }
        
        if (playerObj == null)
        {
            Debug.LogError($"[UnifiedSpawnController] Failed to find player object for client {clientId}");
            playerStates[clientId] = PlayerSpawnState.Failed;
            yield break;
        }
        
        DebugLog($"[FindPlayerObject] Found player object for client {clientId}: {playerObj.name}");
        playerObjects[clientId] = playerObj;
        
        // Start the positioning process
        Coroutine spawnCoroutine = StartCoroutine(PositionPlayerAsync(clientId, playerObj));
        spawnCoroutines[clientId] = spawnCoroutine;
    }

    private GameObject FindPlayerObject(ulong clientId)
    {
        if (NetworkManager.Singleton == null) return null;
        
        foreach (var netObj in NetworkManager.Singleton.SpawnManager.SpawnedObjects.Values)
        {
            if (netObj.IsPlayerObject && netObj.OwnerClientId == clientId)
            {
                return netObj.gameObject;
            }
        }
        
        return null;
    }
    #endregion

    #region Main Spawn Flow
    private IEnumerator PositionPlayerAsync(ulong clientId, GameObject playerObj)
    {
        DebugLog($"[PositionPlayerAsync] Starting spawn flow for client {clientId}");
        
        // ===== PHASE 1: Wait for World Initialization =====
        playerStates[clientId] = PlayerSpawnState.WaitingForWorld;
        UpdateLoadingUI(0.3f, "Initializing world...");
        
        float worldWaitStart = Time.time;
        while (Time.time - worldWaitStart < worldInitTimeout)
        {
            if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized &&
                World.Instance != null && ChunkOperationsQueue.Instance != null)
            {
                break;
            }
            
            float progress = 0.3f + (Time.time - worldWaitStart) / worldInitTimeout * 0.2f;
            UpdateLoadingUI(progress, "Waiting for world initialization...");
            yield return new WaitForSeconds(0.1f);
        }
        
        // Check if timeout occurred
        if (WorldSaveManager.Instance == null || !WorldSaveManager.Instance.IsInitialized ||
            World.Instance == null || ChunkOperationsQueue.Instance == null)
        {
            Debug.LogError($"[UnifiedSpawnController] World initialization timeout for client {clientId}");
            playerStates[clientId] = PlayerSpawnState.Failed;
            UpdateLoadingUI(0f, "World initialization failed");
            yield break;
        }
        
        DebugLog($"[Phase 1] World initialized for client {clientId}");
        
        // ===== PHASE 2: Determine Spawn Position =====
        playerStates[clientId] = PlayerSpawnState.DeterminingPosition;
        UpdateLoadingUI(0.5f, "Determining spawn position...");
        
        Vector3 targetSpawnPosition = DetermineSpawnPosition(clientId);
        DebugLog($"[Phase 2] Determined spawn position for client {clientId}: {targetSpawnPosition}");
        
        // ===== PHASE 3: Register with World (CRITICAL) =====
        playerStates[clientId] = PlayerSpawnState.RegisteringWithWorld;
        UpdateLoadingUI(0.55f, "Registering player with world...");
        
        RegisterPlayerWithWorld(clientId, targetSpawnPosition);
        DebugLog($"[Phase 3] Registered client {clientId} with World at {targetSpawnPosition}");
        
        // ===== PHASE 4: Load Terrain =====
        playerStates[clientId] = PlayerSpawnState.LoadingTerrain;
        UpdateLoadingUI(0.6f, "Loading terrain...");
        
        yield return LoadSpawnTerrain(targetSpawnPosition);
        DebugLog($"[Phase 4] Terrain loaded for client {clientId}");
        
        // NOW that chunks are loaded, verify and adjust ground level if needed
        
        // First, check if spawn position is in a valid air space (cave/tunnel/above ground)
        bool isInAirSpace = !Physics.CheckSphere(targetSpawnPosition, 0.3f, terrainLayer);
        
        if (isInAirSpace)
        {
            // Position is in valid air space - try to verify ground below
            if (Physics.Raycast(targetSpawnPosition + Vector3.up * 5f, Vector3.down, out RaycastHit hitDown, 20f, terrainLayer))
            {
                Vector3 groundPosition = hitDown.point + Vector3.up * minHeightAboveGround;
                float distanceToGround = targetSpawnPosition.y - groundPosition.y;
                
                // Only adjust if significantly above ground (more than 3 blocks)
                if (distanceToGround > 3f)
                {
                    DebugLog($"[Phase 4] Player spawn {distanceToGround:F1}m above ground, adjusting from {targetSpawnPosition.y} to {groundPosition.y}");
                    targetSpawnPosition = groundPosition;
                }
                else
                {
                    // Close to ground or underground in valid air space (cave/tunnel)
                    DebugLog($"[Phase 4] Position verified in valid air space at {targetSpawnPosition} (underground tunnel/cave or near ground)");
                }
            }
            else
            {
                // No ground found below within 20 blocks - probably in deep cave/mine
                DebugLog($"[Phase 4] Position in air space, no immediate ground below (deep cave/mine). Keeping position at {targetSpawnPosition}");
            }
        }
        else
        {
            // Position is INSIDE solid terrain - need to find valid spawn
            Debug.LogWarning($"[Phase 4] Saved position is inside solid terrain at {targetSpawnPosition}");
            
            // Try finding surface above
            if (Physics.Raycast(targetSpawnPosition, Vector3.up, out RaycastHit hitUp, 150f, terrainLayer))
            {
                Vector3 surfacePosition = hitUp.point + Vector3.up * minHeightAboveGround;
                Debug.LogWarning($"[Phase 4] Moving player to surface above at {surfacePosition}");
                targetSpawnPosition = surfacePosition;
            }
            // Try vertical search at this X/Z location
            else
            {
                Debug.LogWarning($"[Phase 4] No surface found above, using vertical search at X/Z location");
                Vector3 searchResult = FindValidSpawnPositionWithVerticalSearch(new Vector3(targetSpawnPosition.x, 0, targetSpawnPosition.z));
                if (searchResult.y > 0)
                {
                    DebugLog($"[Phase 4] Found valid position via vertical search at {searchResult}");
                    targetSpawnPosition = searchResult;
                }
                else
                {
                    Debug.LogError($"[Phase 4] Could not find valid spawn position, keeping at {targetSpawnPosition} - player may be stuck!");
                }
            }
        }
        
        // ===== PHASE 5: Set Position Authoritatively =====
        playerStates[clientId] = PlayerSpawnState.SettingPosition;
        UpdateLoadingUI(0.85f, "Setting player position...");
        
        SetPlayerPositionAuthority(playerObj, targetSpawnPosition, clientId);
        DebugLog($"[Phase 5] Position set for client {clientId}");
        
        // ===== PHASE 6: Activate Components =====
        playerStates[clientId] = PlayerSpawnState.ActivatingComponents;
        UpdateLoadingUI(0.9f, "Activating player...");
        
        yield return ActivatePlayerComponents(playerObj, clientId);
        DebugLog($"[Phase 6] Components activated for client {clientId}");
        
        // ===== COMPLETE =====
        playerStates[clientId] = PlayerSpawnState.Active;
        UpdateLoadingUI(1.0f, "Ready!");
        
        // Hide loading UI after brief delay
        yield return new WaitForSeconds(0.2f);
        UpdateLoadingUI(0f, "", false);
        
        DebugLog($"[PositionPlayerAsync] Spawn flow complete for client {clientId}");
        
        // Clean up coroutine reference
        spawnCoroutines.Remove(clientId);
    }
    #endregion

    #region Phase 2: Position Determination
    private Vector3 DetermineSpawnPosition(ulong clientId)
    {
        // Try to load saved position first
        Vector3 savedPosition = Vector3.zero;
        
        if (PlayerSpawner.Instance != null)
        {
            savedPosition = PlayerSpawner.Instance.GetClientLastPosition(clientId);
        }
        
        // CRITICAL: Validate saved position before using it
        // Prevents spawning at invalid positions from corrupted save files or fall sessions
        if (savedPosition != Vector3.zero)
        {
            // Check if Y coordinate is within reasonable bounds
            // Don't do raycast validation here - chunks aren't loaded yet!
            if (savedPosition.y < -50f)
            {
                Debug.LogWarning($"[Phase 2] Saved position for client {clientId} is too far underground (Y={savedPosition.y}), using default spawn position");
                savedPosition = Vector3.zero;
            }
            else if (savedPosition.y > 300f)
            {
                Debug.LogWarning($"[Phase 2] Saved position for client {clientId} is absurdly high (Y={savedPosition.y}), using default spawn position");
                savedPosition = Vector3.zero;
            }
            else
            {
                // Y coordinate is reasonable - trust it!
                // We'll verify actual ground exists in Phase 4 after chunks load
                DebugLog($"[Phase 2] Using saved position for client {clientId}: {savedPosition}");
                return savedPosition;
            }
        }
        
        // No saved position - use default spawn position with safe height
        // IMPORTANT: Don't do vertical search here - chunks aren't loaded yet (Phase 2 before Phase 4)
        // Raycasts will fail on non-existent terrain
        Vector3 safeSpawnPosition = new Vector3(defaultSpawnPosition.x, 60f, defaultSpawnPosition.z);
        DebugLog($"No saved position for client {clientId}, using default spawn at safe height: {safeSpawnPosition}");
        return safeSpawnPosition;
    }

    private Vector3 FindValidSpawnPositionWithVerticalSearch(Vector3 startPosition)
    {
        DebugLog($"[VerticalSearch] Starting search from {startPosition}");
        
        // Test positions at different heights
        foreach (float heightOffset in verticalSearchHeights)
        {
            Vector3 testPos = new Vector3(
                startPosition.x,
                startPosition.y + heightOffset,
                startPosition.z
            );
            
            // Cast ray downward to find ground
            if (Physics.Raycast(testPos, Vector3.down, out RaycastHit hit, maxRaycastDistance, terrainLayer))
            {
                Vector3 groundPos = hit.point + (Vector3.up * minHeightAboveGround);
                DebugLog($"[VerticalSearch] Found ground at height offset {heightOffset}: {groundPos}");
                return groundPos;
            }
        }
        
        DebugLog($"[VerticalSearch] No ground found with vertical search, trying spiral search");
        
        // If no ground found, try spiral search
        Vector3 spiralResult = FindGroundInSpiral(startPosition + Vector3.up * 100f);
        if (spiralResult != Vector3.zero)
        {
            DebugLog($"[VerticalSearch] Found ground with spiral search: {spiralResult}");
            return spiralResult;
        }
        
        // Emergency fallback: High position
        Vector3 emergencyPos = new Vector3(startPosition.x, 100f, startPosition.z);
        Debug.LogWarning($"[VerticalSearch] No ground found after exhaustive search, using emergency height: {emergencyPos}");
        return emergencyPos;
    }

    private Vector3 FindGroundInSpiral(Vector3 center)
    {
        // Search in expanding spiral
        for (int ring = 0; ring <= spiralSearchRings; ring++)
        {
            int segments = ring * 8;
            if (segments < 8) segments = 8; // Minimum 8 segments for the first ring
            
            for (int segment = 0; segment < segments; segment++)
            {
                float angle = (segment / (float)segments) * Mathf.PI * 2f;
                float radius = ring * spiralSearchSpacing;
                
                Vector3 offset = new Vector3(
                    Mathf.Cos(angle) * radius,
                    0f,
                    Mathf.Sin(angle) * radius
                );
                
                Vector3 testPoint = center + offset;
                
                // Try to find ground below this point
                if (Physics.Raycast(testPoint, Vector3.down, out RaycastHit hit, maxRaycastDistance, terrainLayer))
                {
                    return hit.point + Vector3.up * minHeightAboveGround;
                }
            }
        }
        
        return Vector3.zero;
    }
    #endregion

    #region Phase 3: World Registration (CRITICAL)
    private void RegisterPlayerWithWorld(ulong clientId, Vector3 position)
    {
        if (World.Instance == null)
        {
            Debug.LogError($"[Phase 3] World.Instance is null, cannot register player {clientId}");
            return;
        }
        
        // CRITICAL: Register player position FIRST before any chunk operations
        // This adds the player to playerChunkCoordinates dictionary
        World.Instance.UpdatePlayerPositionForClient(clientId, position);
        
        // Force immediate recalculation of active chunks
        // This protects spawn area from unloading
        World.Instance.RecalculateActiveChunks();
        
        DebugLog($"[Phase 3] Registered player {clientId} with World at {position}");
        DebugLog($"[Phase 3] Active chunks recalculated to protect spawn area");
    }
    #endregion

    #region Phase 4: Terrain Loading
    private IEnumerator LoadSpawnTerrain(Vector3 position)
    {
        if (World.Instance == null || ChunkOperationsQueue.Instance == null)
        {
            Debug.LogWarning("[Phase 4] World or ChunkOperationsQueue not available");
            yield break;
        }
        
        // Calculate chunk coordinate
        Vector3Int chunkCoord = Coord.WorldToChunkCoord(
            position,
            World.Instance.chunkSize,
            World.Instance.voxelSize
        );
        
        DebugLog($"[Phase 4] Loading chunks around {chunkCoord}");
        
        // Queue chunks for loading (5x5x5 area)
        int totalChunks = 0;
        for (int x = -spawnChunkRadius; x <= spawnChunkRadius; x++)
        {
            for (int y = -spawnChunkRadius; y <= spawnChunkRadius; y++)
            {
                for (int z = -spawnChunkRadius; z <= spawnChunkRadius; z++)
                {
                    Vector3Int coord = chunkCoord + new Vector3Int(x, y, z);
                    
                    if (!World.Instance.IsChunkLoaded(coord))
                    {
                        bool isImmediate = Mathf.Abs(x) <= centerChunkRadius && 
                                          Mathf.Abs(y) <= centerChunkRadius && 
                                          Mathf.Abs(z) <= centerChunkRadius;
                        
                        ChunkOperationsQueue.Instance.QueueChunkForLoad(coord, isImmediate, false);
                        totalChunks++;
                    }
                }
            }
        }
        
        DebugLog($"[Phase 4] Queued {totalChunks} chunks for loading");
        
        // Wait for center chunks to load (3x3x3)
        float loadStart = Time.time;
        int centerChunkCount = (centerChunkRadius * 2 + 1) * (centerChunkRadius * 2 + 1) * (centerChunkRadius * 2 + 1);
        
        while (Time.time - loadStart < chunkLoadTimeout)
        {
            int loadedCount = 0;
            
            for (int x = -centerChunkRadius; x <= centerChunkRadius; x++)
            {
                for (int y = -centerChunkRadius; y <= centerChunkRadius; y++)
                {
                    for (int z = -centerChunkRadius; z <= centerChunkRadius; z++)
                    {
                        Vector3Int coord = chunkCoord + new Vector3Int(x, y, z);
                        if (World.Instance.IsChunkLoaded(coord))
                        {
                            loadedCount++;
                        }
                    }
                }
            }
            
            // Update progress
            float progress = 0.6f + (loadedCount / (float)centerChunkCount) * 0.25f;
            UpdateLoadingUI(progress, $"Loading terrain... {loadedCount}/{centerChunkCount} chunks");
            
            // Check if all center chunks loaded
            if (loadedCount >= centerChunkCount)
            {
                DebugLog($"[Phase 4] All {centerChunkCount} center chunks loaded");
                break;
            }
            
            yield return new WaitForSeconds(chunkLoadCheckInterval);
        }
        
        // Check if we timed out
        if (Time.time - loadStart >= chunkLoadTimeout)
        {
            Debug.LogWarning($"[Phase 4] Chunk loading timeout, continuing anyway");
        }
        
        // CRITICAL: Wait additional time for collision to be ready
        // Chunks can be "loaded" but mesh/collision generation happens async
        DebugLog($"[Phase 4] Waiting {postLoadWaitTime}s for chunk collision to be ready");
        yield return new WaitForSeconds(postLoadWaitTime);
        
        DebugLog($"[Phase 4] Chunk collision should now be ready");
    }
    #endregion

    #region Phase 5: Position Setting
    private void SetPlayerPositionAuthority(GameObject playerObj, Vector3 position, ulong clientId)
    {
        // CRITICAL: Completely disable all movement and physics
        var thirdPersonController = playerObj.GetComponent<ThirdPersonController>();
        var characterController = playerObj.GetComponent<CharacterController>();
        var playerInput = playerObj.GetComponent<PlayerInput>();
        var rigidbody = playerObj.GetComponent<Rigidbody>();
        
        // Disable all control components
        if (thirdPersonController != null) thirdPersonController.enabled = false;
        if (playerInput != null) playerInput.enabled = false;
        
        // Disable CharacterController completely (it applies gravity even when disabled in some cases)
        if (characterController != null)
        {
            characterController.enabled = false;
        }
        
        // Disable rigidbody physics if present
        if (rigidbody != null)
        {
            rigidbody.isKinematic = true;
            rigidbody.useGravity = false;
        }
        
        DebugLog($"[Phase 5] Disabled ALL movement and physics for client {clientId}");
        
        // Add temporary gravity suspension as backup
        var gravitySuspension = playerObj.GetComponent<TemporaryGravitySuspension>();
        if (gravitySuspension == null)
        {
            gravitySuspension = playerObj.AddComponent<TemporaryGravitySuspension>();
        }
        gravitySuspension.SuspendGravity(gravitySuspensionDuration);
        
        DebugLog($"[Phase 5] Added gravity suspension for {gravitySuspensionDuration} seconds");
        
        // Set position directly (CharacterController already disabled)
        playerObj.transform.position = position;
        
        // Update NetworkTransform
        var netTransform = playerObj.GetComponent<NetworkTransform>();
        if (netTransform != null)
        {
            netTransform.Teleport(position, Quaternion.identity, playerObj.transform.localScale);
        }
        
        // Update NetworkPlayer NetworkVariable (server authority)
        var networkPlayer = playerObj.GetComponent<NetworkPlayer>();
        if (networkPlayer != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            networkPlayer.SetNetworkPositionAuthority(position, Quaternion.identity);
        }
        
        DebugLog($"[Phase 5] Set position authority for client {clientId} at {position}");
        
        // Save position
        if (PlayerSpawner.Instance != null)
        {
            PlayerSpawner.Instance.SavePlayerPosition(clientId, position);
        }
    }
    #endregion

    #region Phase 6: Component Activation
    private IEnumerator ActivatePlayerComponents(GameObject playerObj, ulong clientId)
    {
        // Wait longer for chunks to have collision, not just be "loaded"
        // Chunks can be "loaded" but mesh/collision might not be ready yet
        yield return new WaitForSeconds(0.5f);
        
        DebugLog($"[Phase 6] Waited 0.5s for chunk collision to be ready");
        
        // Wait additional physics frames
        for (int i = 0; i < physicsSettleFrames; i++)
        {
            yield return null;
        }
        
        DebugLog($"[Phase 6] Waited {physicsSettleFrames} additional frames for physics");
        
        // Verify player hasn't fallen (sanity check)
        Vector3 currentPos = playerObj.transform.position;
        float spawnY = currentPos.y;
        
        // Re-enable rigidbody if present
        var rigidbody = playerObj.GetComponent<Rigidbody>();
        if (rigidbody != null)
        {
            rigidbody.isKinematic = false;
            rigidbody.useGravity = true;
            DebugLog($"[Phase 6] Re-enabled rigidbody physics for client {clientId}");
        }
        
        // Enable movement components
        var thirdPersonController = playerObj.GetComponent<ThirdPersonController>();
        var characterController = playerObj.GetComponent<CharacterController>();
        var playerInput = playerObj.GetComponent<PlayerInput>();
        
        if (characterController != null) characterController.enabled = true;
        if (thirdPersonController != null) thirdPersonController.enabled = true;
        if (playerInput != null) playerInput.enabled = true;
        
        DebugLog($"[Phase 6] Enabled movement components for client {clientId}");
        
        // Enable fall recovery system
        var fallRecovery = playerObj.GetComponent<PlayerFallRecovery>();
        if (fallRecovery != null)
        {
            fallRecovery.enabled = true;
            DebugLog($"[Phase 6] Enabled fall recovery for client {clientId}");
        }
        
        // REMOVED: PlayerSpawnSafety disable logic (component deleted)
        // PlayerSpawnSafety has been removed - its functionality is now in UnifiedSpawnController
        
        // Final registration with World
        if (World.Instance != null)
        {
            World.Instance.UpdatePlayerPositionForClient(clientId, playerObj.transform.position);
        }
        
        // Register with PlayerSpawner for ongoing position tracking
        if (PlayerSpawner.Instance != null)
        {
            PlayerSpawner.Instance.RegisterPlayerWithWorld(clientId, playerObj);
        }
        
        DebugLog($"[Phase 6] Player {clientId} fully activated at position {playerObj.transform.position}");
        
        // Log if player fell during spawn (shouldn't happen)
        if (Mathf.Abs(playerObj.transform.position.y - spawnY) > 1f)
        {
            Debug.LogWarning($"[Phase 6] WARNING: Player {clientId} moved {Mathf.Abs(playerObj.transform.position.y - spawnY)}m vertically during activation!");
        }
    }
    #endregion

    #region Loading UI
    private void UpdateLoadingUI(float progress, string status, bool visible = true)
    {
        var uiManager = FindFirstObjectByType<GameUIManager>();
        if (uiManager != null)
        {
            uiManager.SetGameplayLoadingOverlay(visible, progress, status);
        }
    }
    #endregion

    #region Public API
    public PlayerSpawnState GetPlayerState(ulong clientId)
    {
        return playerStates.TryGetValue(clientId, out PlayerSpawnState state) ? state : PlayerSpawnState.NotSpawned;
    }

    public bool IsPlayerActive(ulong clientId)
    {
        return GetPlayerState(clientId) == PlayerSpawnState.Active;
    }
    #endregion

    #region Debug
    private void DebugLog(string message)
    {
        if (showDebugLogs)
        {
            Debug.Log($"[UnifiedSpawnController] {message}");
        }
    }
    #endregion
}

