using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using NelsUtils;
using System;
using ControllerAssets;

public class PlayerSpawner : MonoBehaviour
{
    [Header("Spawn Settings")]
    [SerializeField] private float maxSpawnHeight = 128f;
    [SerializeField] private float groundDetectionDistance = 150f;
    [SerializeField] private int spawnCheckRadius = 5;
    [SerializeField] private LayerMask terrainLayer;
    [SerializeField] private Vector3 defaultSpawnPosition = Vector3.zero;
    [SerializeField] private float minHeightAboveGround = 2f;
    
    [Header("Player Save/Load")]
    [SerializeField] private bool loadPreviousPosition = true;
    [SerializeField] private string playerPositionKey = "PlayerPosition";
    
    [Header("Integration")]
    [SerializeField] private bool autoRegisterWithNetworkManager = true;
    // REMOVED: Unused field with UnifiedSpawnController
    // [SerializeField] private bool prioritizeSpawnTerrainLoading = true;
    
    [Header("Debugging")]
    [SerializeField] private bool showDebugLogs = true;
    
    // Internal state
    private Dictionary<ulong, Vector3> pendingSpawnPositions = new Dictionary<ulong, Vector3>();
    private Dictionary<ulong, bool> clientPositionLoaded = new Dictionary<ulong, bool>();
    private Dictionary<ulong, GameObject> playerObjects = new Dictionary<ulong, GameObject>();
    private Dictionary<ulong, bool> clientTeleportedToSavedPosition = new Dictionary<ulong, bool>(); // Track if player was teleported to saved position
    private static PlayerSpawner instance;
    
    public static PlayerSpawner Instance { get { return instance; } }
    
    // Events - Removed: OnPlayerSpawnPositionFound (no longer used with UnifiedSpawnController)
    // public delegate void PlayerSpawnDelegate(ulong clientId, Vector3 position);
    // public event PlayerSpawnDelegate OnPlayerSpawnPositionFound;
    
    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
        
        // Set up terrain layer if not assigned
        if (terrainLayer == 0)
        {
            terrainLayer = LayerMask.GetMask("Terrain");
        }
    }
    
    private void Start()
    {
        // MODIFIED: PlayerSpawner is now a helper class only
        // UnifiedSpawnController handles connection approval and spawn coordination
        // This class just provides save/load functionality
        
        if (autoRegisterWithNetworkManager && NetworkManager.Singleton != null)
        {
            // Only register for disconnect to clean up data
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            
            DebugLog("PlayerSpawner registered as helper (UnifiedSpawnController handles spawn coordination)");
        }
    }
    
    private void OnDestroy()
    {
        // Unregister from network events
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }
    
    // REMOVED: HandleConnectionApproval - now handled by UnifiedSpawnController
    // REMOVED: OnClientConnected - spawn coordination now handled by UnifiedSpawnController
    
    private void OnClientDisconnected(ulong clientId)
    {
        // Save position before removing player
        if (playerObjects.TryGetValue(clientId, out GameObject playerObj) && playerObj != null)
        {
            SavePlayerPosition(clientId, playerObj.transform.position);
        }
        
        // Clean up references
        playerObjects.Remove(clientId);
        pendingSpawnPositions.Remove(clientId);
        clientPositionLoaded.Remove(clientId);
        clientTeleportedToSavedPosition.Remove(clientId);
        
        DebugLog($"Client {clientId} disconnected, cleaned up references");
    }
    
    // REMOVED: FindPlayerObjectDelayed - now handled by UnifiedSpawnController
    // REMOVED: FindPlayerObject - now handled by UnifiedSpawnController
    
    public void RegisterPlayerWithWorld(ulong clientId, GameObject playerObject)
    {
        if (playerObject == null)
            return;
            
        // Store the reference
        playerObjects[clientId] = playerObject;
        
        // Only save position if NOT already teleported to saved position
        // (teleporting to saved position already saved it)
        bool alreadyTeleported = clientTeleportedToSavedPosition.TryGetValue(clientId, out bool wasTeleported) && wasTeleported;
        if (!alreadyTeleported)
        {
            Debug.Log($"[PlayerSpawner] Saving initial spawn position for client {clientId}: {playerObject.transform.position}");
            // Save the initial position
            SavePlayerPosition(clientId, playerObject.transform.position);
        }
        else
        {
            Debug.Log($"[PlayerSpawner] Skipping position save for client {clientId} - already teleported to saved position");
        }
        
        // Mark as loaded
        clientPositionLoaded[clientId] = true;
        
        DebugLog($"Registered player {clientId} with world save system");
    }

    public Vector3 GetPlayerSpawnPosition(ulong clientId)
    {
        // First check if we have saved data for this player
        Vector3 savedPosition = GetClientLastPosition(clientId);
        
        // If we have a valid saved position, use it
        if (savedPosition != Vector3.zero)
        {
            DebugLog($"Using saved position for player {clientId}: {savedPosition}");
            return savedPosition;
        }
        
        // Otherwise, use default spawn position with ground detection
        Vector3 spawnPos = FindValidSpawnPosition(defaultSpawnPosition);
        if (spawnPos != Vector3.zero)
        {
            DebugLog($"Using found spawn position for new player {clientId}: {spawnPos}");
            return spawnPos;
        }
        
        // If all else fails, return a safe height above default
        DebugLog($"Using default spawn position for new player {clientId}");
        return new Vector3(defaultSpawnPosition.x, maxSpawnHeight / 2, defaultSpawnPosition.z);
    }

    public bool IsServerOnlyMode()
    {
        return NetworkManager.Singleton != null && 
            NetworkManager.Singleton.IsServer && 
            !NetworkManager.Singleton.IsHost;
    }
    
    /// <summary>
    /// Check if a player was teleported to a saved position (should skip spawn safety)
    /// </summary>
    public bool WasPlayerTeleportedToSavedPosition(ulong clientId)
    {
        return clientTeleportedToSavedPosition.TryGetValue(clientId, out bool wasTeleported) && wasTeleported;
    }

    public void InitializeServerMode()
    {
        if (!IsServerOnlyMode())
            return;
            
        DebugLog("Initializing server-only mode");
        
        // In server mode, we won't have a local player, but we can create a default position
        // for terrain generation around origin
        Vector3 defaultServerPosition = Vector3.zero;
        
        // Register this position with World
        if (World.Instance != null)
        {
            World.Instance.UpdatePlayerPosition(defaultServerPosition);
            DebugLog($"Set default server position for world: {defaultServerPosition}");
        }
    }

    public Vector3 GetClientLastPosition(ulong clientId)
    {
        Debug.Log($"[PlayerSpawner] GetClientLastPosition called for client {clientId}");
        Debug.Log($"[PlayerSpawner] loadPreviousPosition = {loadPreviousPosition}");
        
        if (!loadPreviousPosition)
        {
            Debug.LogWarning($"[PlayerSpawner] loadPreviousPosition is DISABLED! Returning Vector3.zero");
            return Vector3.zero;
        }
            
        try
        {
            // First try WorldSaveManager
            if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
            {
                Debug.Log($"[PlayerSpawner] WorldSaveManager is initialized, attempting to load player data for client {clientId}");
                var playerData = WorldSaveManager.Instance.LoadPlayerData(clientId);
                if (playerData != null)
                {
                    Vector3 savedPos = playerData.Position;
                    Debug.Log($"[PlayerSpawner] ✅ SUCCESSFULLY loaded player position from WorldSaveManager for client {clientId}: {savedPos}");
                    DebugLog($"Loaded player position from WorldSaveManager for client {clientId}: {savedPos}");
                    return savedPos;
                }
                else
                {
                    Debug.Log($"[PlayerSpawner] No saved player data found in WorldSaveManager for client {clientId}");
                }
            }
            else
            {
                Debug.LogWarning($"[PlayerSpawner] WorldSaveManager is null or not initialized! Instance={WorldSaveManager.Instance}, Initialized={WorldSaveManager.Instance?.IsInitialized}");
            }

            // Fall back to PlayerPrefs for backward compatibility
            string key = $"{playerPositionKey}_{clientId}";
            Debug.Log($"[PlayerSpawner] Checking PlayerPrefs with key: {key}");
            
            if (PlayerPrefs.HasKey(key))
            {
                string posData = PlayerPrefs.GetString(key, "");
                Debug.Log($"[PlayerSpawner] Found PlayerPrefs data: {posData}");
                
                if (!string.IsNullOrEmpty(posData))
                {
                    string[] components = posData.Split(',');
                    if (components.Length == 3)
                    {
                        float x = float.Parse(components[0]);
                        float y = float.Parse(components[1]);
                        float z = float.Parse(components[2]);
                        Vector3 loadedPos = new Vector3(x, y, z);
                        Debug.Log($"[PlayerSpawner] ✅ Loaded position from PlayerPrefs for client {clientId}: {loadedPos}");
                        return loadedPos;
                    }
                }
            }
            else
            {
                Debug.Log($"[PlayerSpawner] No PlayerPrefs data found for key: {key}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[PlayerSpawner] Error loading player position: {e.Message}\n{e.StackTrace}");
        }
        
        Debug.Log($"[PlayerSpawner] No saved position found for client {clientId}, returning Vector3.zero");
        return Vector3.zero;
    }
    
    /// <summary>
    /// Save player's current position for future spawning
    /// </summary>
    public void SavePlayerPosition(ulong clientId, Vector3 position)
    {
        if (!loadPreviousPosition)
            return;
            
        try
        {
            // First save to PlayerPrefs for backward compatibility
            string key = $"{playerPositionKey}_{clientId}";
            string posData = $"{position.x},{position.y},{position.z}";
            PlayerPrefs.SetString(key, posData);
            PlayerPrefs.Save();
            
            // Also save to WorldSaveManager if available
            if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
            {
                // Get player name if possible
                string playerName = $"Player_{clientId}";
                
                // Get GameObject for rotation if available
                Quaternion rotation = Quaternion.identity;
                if (playerObjects.TryGetValue(clientId, out GameObject playerObj) && playerObj != null)
                {
                    rotation = playerObj.transform.rotation;
                }
                
                // Save to world manager
                WorldSaveManager.Instance.SavePlayerData(clientId, playerName, position, rotation);
                DebugLog($"Saved player data to WorldSaveManager for client {clientId}");
            }
            
            DebugLog($"Saved position for client {clientId}: {position}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Error saving player position: {e.Message}");
        }
    }
    
    // REMOVED: PrepareSpawnLocation - now handled by UnifiedSpawnController Phase 4

    // MODIFY EnsureTerrainLoaded method in PlayerSpawner.cs
    private void EnsureTerrainLoaded(Vector3 position)
    {
        if (World.Instance == null)
        {
            Debug.LogWarning("World instance not available for terrain loading");
            return;
        }
        
        // Use the position to trigger World's chunk loading system
        World.Instance.UpdatePlayerPosition(position);
        
        // Wait a frame to let chunk loading queue
        StartCoroutine(PrioritizeTerrainLoadingCoroutine(position));
    }
    
    private IEnumerator PrioritizeTerrainLoadingCoroutine(Vector3 position)
    {
        yield return null;
        
        if (World.Instance == null || ChunkOperationsQueue.Instance == null)
        {
            yield break;
        }
        
        // Priority load the immediate chunks
        Vector3Int chunkCoord = Coord.WorldToChunkCoord(
            position, 
            World.Instance.chunkSize, 
            World.Instance.voxelSize
        );
        
        // Load immediate chunks with high priority
        for (int x = -2; x <= 2; x++)  // Increased radius from 1 to 2
        for (int y = -2; y <= 2; y++)  // Increased radius from 1 to 2
        for (int z = -2; z <= 2; z++)  // Increased radius from 1 to 2
        {
            Vector3Int coord = chunkCoord + new Vector3Int(x, y, z);
            
            if (!World.Instance.IsChunkLoaded(coord))
            {
                // Use immediate loading for spawn chunks and disable quick check
                ChunkOperationsQueue.Instance.QueueChunkForLoad(coord, true, false);
                Debug.Log($"Priority loading chunk {coord} for player spawn");
            }
        }
        
        // Wait a moment, then invalidate terrain analysis for immediate chunks
        yield return new WaitForSeconds(0.5f);
        
        // Force invalidate terrain analysis for immediate chunks
        for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
        for (int z = -1; z <= 1; z++)
        {
            Vector3Int coord = chunkCoord + new Vector3Int(x, y, z);
            TerrainAnalysisCache.InvalidateAnalysis(coord);
            Debug.Log($"Invalidated terrain analysis for spawn chunk {coord}");
        }
    }
    
    private Vector3 FindGroundBelow(Vector3 position)
    {
        // Cast a ray downward to find ground
        if (Physics.Raycast(position, Vector3.down, out RaycastHit hit, groundDetectionDistance, terrainLayer))
        {
            DebugLog($"Found ground at {hit.point}, distance: {hit.distance}");
            
            // Return a position slightly above the hit point
            return hit.point + Vector3.up * minHeightAboveGround;
        }
        
        // If we're near zero height, cast upward too in case we're underground
        if (position.y < 10f)
        {
            if (Physics.Raycast(position, Vector3.up, out hit, 50f, terrainLayer))
            {
                DebugLog($"Found ceiling at {hit.point}, distance: {hit.distance}");
                
                // Return a position below the hit point (trying to get out from under terrain)
                return new Vector3(position.x, hit.point.y + 30f, position.z);
            }
        }
        
        return Vector3.zero; // No ground found
    }
    
    private Vector3 FindValidSpawnPosition(Vector3 initialPosition)
    {
        if (World.Instance == null)
        {
            return new Vector3(initialPosition.x, maxSpawnHeight / 2, initialPosition.z);
        }
        
        // First try at the initial position itself
        Vector3 position = FindGroundBelow(initialPosition);
        if (position != Vector3.zero)
            return position;
            
        // Then try at various heights
        for (float y = 10f; y < maxSpawnHeight; y += 20f)
        {
            Vector3 testPos = new Vector3(initialPosition.x, y, initialPosition.z);
            position = FindGroundBelow(testPos);
            if (position != Vector3.zero)
                return position;
        }
        
        // Search in a spiral pattern at different heights
        int[] xOffsets = { 0, 1, 0, -1, 1, 1, -1, -1 };
        int[] zOffsets = { 1, 0, -1, 0, 1, -1, -1, 1 };
        
        for (float y = 20f; y < maxSpawnHeight; y += 40f)
        {
            for (int i = 1; i <= spawnCheckRadius; i++)
            {
                for (int j = 0; j < xOffsets.Length; j++)
                {
                    Vector3 offset = new Vector3(
                        xOffsets[j] * i * World.Instance.voxelSize * 5f,
                        0,
                        zOffsets[j] * i * World.Instance.voxelSize * 5f
                    );
                    
                    Vector3 testPos = new Vector3(initialPosition.x, y, initialPosition.z) + offset;
                    position = FindGroundBelow(testPos);
                    
                    if (position != Vector3.zero)
                        return position;
                }
            }
        }
        
        // Last resort: Return the initial position with a safe height
        return Vector3.zero;
    }
    
    // REMOVED: GetSpawnPosition - spawn position determination now handled by UnifiedSpawnController Phase 2
    
    public void RegisterPlayerSpawn(ulong clientId, Vector3 position)
    {
        pendingSpawnPositions[clientId] = position;
        clientPositionLoaded[clientId] = true;
        
        // Save the position for next time
        SavePlayerPosition(clientId, position);
        
        DebugLog($"Registered player spawn for client {clientId} at {position}");
    }

    public void TeleportPlayer(ulong clientId, GameObject playerObject, Vector3 position)
    {
        if (playerObject == null)
        {
            Debug.LogError($"Cannot teleport null player object for client {clientId}");
            return;
        }
        
        Debug.Log($"[PlayerSpawner] Teleporting player {clientId} to {position}");
        
        // Disable any CharacterController before teleporting
        CharacterController charController = playerObject.GetComponent<CharacterController>();
        bool wasCharControllerEnabled = false;
        if (charController != null)
        {
            wasCharControllerEnabled = charController.enabled;
            charController.enabled = false;
            Debug.Log($"[PlayerSpawner] Disabled CharacterController for teleport");
        }
        
        // Disable ThirdPersonController if it's enabled (shouldn't be, but just in case)
        var thirdPersonController = playerObject.GetComponent<ThirdPersonController>();
        bool wasTPControllerEnabled = false;
        if (thirdPersonController != null)
        {
            wasTPControllerEnabled = thirdPersonController.enabled;
            thirdPersonController.enabled = false;
            Debug.Log($"[PlayerSpawner] Disabled ThirdPersonController for teleport");
        }
        
        // Set position directly
        playerObject.transform.position = position;
        Debug.Log($"[PlayerSpawner] Set transform.position to {position}");
        
        // DON'T re-enable controllers here - let NetworkPlayerSetup handle that
        // This prevents the CharacterController from doing a physics update before the player is fully initialized
        Debug.Log($"[PlayerSpawner] Leaving controllers disabled - NetworkPlayerSetup will enable them");
        
        // If we have a NetworkTransform, use Teleport() method
        var netTransform = playerObject.GetComponent<Unity.Netcode.Components.NetworkTransform>();
        if (netTransform != null)
        {
            netTransform.Teleport(position, playerObject.transform.rotation, playerObject.transform.localScale);
            Debug.Log($"[PlayerSpawner] Called NetworkTransform.Teleport()");
        }
        
        // CRITICAL: Update NetworkPlayer's networkPosition NetworkVariable
        // This prevents the network sync from reverting the teleport
        var networkPlayer = playerObject.GetComponent<NetworkPlayer>();
        if (networkPlayer != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            networkPlayer.SetNetworkPositionAuthority(position, playerObject.transform.rotation);
            Debug.Log($"[PlayerSpawner] Set NetworkPlayer networkPosition to {position}");
        }
        
        // Update pending position and save it
        pendingSpawnPositions[clientId] = position;
        SavePlayerPosition(clientId, position);
    }
    
    public Vector3 FindGroundForPlayer(ulong clientId, Vector3 currentPosition)
    {
        // Try to find ground below
        Vector3 groundPos = FindGroundBelow(currentPosition);
        
        // If no ground found, try using a saved position
        if (groundPos == Vector3.zero)
        {
            Vector3 savedPos = GetClientLastPosition(clientId);
            if (savedPos != Vector3.zero)
            {
                groundPos = FindGroundBelow(savedPos);
            }
        }
        
        // If still no ground, use default position
        if (groundPos == Vector3.zero)
        {
            groundPos = FindValidSpawnPosition(defaultSpawnPosition);
        }
        
        // If all else fails, just put them safely high up
        if (groundPos == Vector3.zero)
        {
            groundPos = new Vector3(currentPosition.x, maxSpawnHeight / 2, currentPosition.z);
        }
        
        return groundPos;
    }
    
    private void DebugLog(string message)
    {
        if (showDebugLogs)
        {
            Debug.Log($"[PlayerSpawner] {message}");
        }
    }
}