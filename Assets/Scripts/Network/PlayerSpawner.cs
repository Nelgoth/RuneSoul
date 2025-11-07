using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using NelsUtils;
using System;

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
    [SerializeField] private bool prioritizeSpawnTerrainLoading = true;
    
    [Header("Debugging")]
    [SerializeField] private bool showDebugLogs = true;
    
    // Internal state
    private Dictionary<ulong, Vector3> pendingSpawnPositions = new Dictionary<ulong, Vector3>();
    private Dictionary<ulong, bool> clientPositionLoaded = new Dictionary<ulong, bool>();
    private Dictionary<ulong, GameObject> playerObjects = new Dictionary<ulong, GameObject>();
    private static PlayerSpawner instance;
    
    public static PlayerSpawner Instance { get { return instance; } }
    
    // Events
    public delegate void PlayerSpawnDelegate(ulong clientId, Vector3 position);
    public event PlayerSpawnDelegate OnPlayerSpawnPositionFound;
    
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
        // Register with NetworkManager if auto-register is enabled
        if (autoRegisterWithNetworkManager && NetworkManager.Singleton != null)
        {
            // Register to network events
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            
            // Connect to NetworkManager's connection approval
            NetworkManager.Singleton.ConnectionApprovalCallback += HandleConnectionApproval;
            
            DebugLog("Registered with NetworkManager for connection and approval events");
        }
    }
    
    private void OnDestroy()
    {
        // Unregister from network events
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            NetworkManager.Singleton.ConnectionApprovalCallback -= HandleConnectionApproval;
        }
    }
    
    private void HandleConnectionApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // Special handling for server-only mode - no player needed
        if (IsServerOnlyMode() && request.ClientNetworkId == NetworkManager.ServerClientId)
        {
            DebugLog("Server mode detected - no player object needed for the server itself");
            response.Approved = true;
            response.CreatePlayerObject = false; // Don't create player for the server itself
            return;
        }
        
        // Always approve normal clients
        response.Approved = true;
        response.CreatePlayerObject = true;
        
        // Get a spawn position
        Vector3 spawnPosition = GetSpawnPosition(request.ClientNetworkId);
        
        // Set the spawn location
        response.Position = spawnPosition;
        response.Rotation = Quaternion.identity;
        
        DebugLog($"Connection approval for client {request.ClientNetworkId}: position={spawnPosition}");
    }
    
    private void OnClientConnected(ulong clientId)
    {
        if (NetworkManager.Singleton.IsServer)
        {
            DebugLog($"Client {clientId} connected, preparing spawn position");
            
            // If this is a reconnection, try to load the previous position
            Vector3 position = GetClientLastPosition(clientId);
            
            // If position is valid, mark as loaded and use it
            if (position != Vector3.zero && loadPreviousPosition)
            {
                clientPositionLoaded[clientId] = true;
                DebugLog($"Using previous position for client {clientId}: {position}");
                pendingSpawnPositions[clientId] = position;
                StartCoroutine(PrepareSpawnLocation(clientId, position));
            }
            else
            {
                // Otherwise, use default (0,0,0) spawn position
                clientPositionLoaded[clientId] = false;
                position = defaultSpawnPosition;
                DebugLog($"Using default position for client {clientId}: {position}");
                pendingSpawnPositions[clientId] = position;
                StartCoroutine(PrepareSpawnLocation(clientId, position));
            }
            
            // Track the player objects
            StartCoroutine(FindPlayerObjectDelayed(clientId));
        }
    }
    
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
        
        DebugLog($"Client {clientId} disconnected, cleaned up references");
    }
    
    private IEnumerator FindPlayerObjectDelayed(ulong clientId)
    {
        // Wait a few frames for the player object to spawn
        yield return new WaitForSeconds(0.5f);
        
        GameObject playerObj = FindPlayerObject(clientId);
        if (playerObj != null)
        {
            playerObjects[clientId] = playerObj;
            DebugLog($"Found player object for client {clientId}: {playerObj.name}");
        }
        else
        {
            // Try again after a longer delay
            yield return new WaitForSeconds(1.0f);
            playerObj = FindPlayerObject(clientId);
            if (playerObj != null)
            {
                playerObjects[clientId] = playerObj;
                DebugLog($"Found player object for client {clientId} after delay: {playerObj.name}");
            }
            else
            {
                Debug.LogWarning($"Failed to find player object for client {clientId}");
            }
        }
    }
    
    private GameObject FindPlayerObject(ulong clientId)
    {
        // Look through network spawn manager
        foreach (var netObj in NetworkManager.Singleton.SpawnManager.SpawnedObjects.Values)
        {
            if (netObj.IsPlayerObject && netObj.OwnerClientId == clientId)
            {
                return netObj.gameObject;
            }
        }
        
        return null;
    }
    
    public void RegisterPlayerWithWorld(ulong clientId, GameObject playerObject)
    {
        if (playerObject == null)
            return;
            
        // Store the reference
        playerObjects[clientId] = playerObject;
        
        // Save the initial position
        SavePlayerPosition(clientId, playerObject.transform.position);
        
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

    private Vector3 GetClientLastPosition(ulong clientId)
    {
        if (!loadPreviousPosition)
            return Vector3.zero;
            
        try
        {
            // First try WorldSaveManager
            if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
            {
                var playerData = WorldSaveManager.Instance.LoadPlayerData(clientId);
                if (playerData != null)
                {
                    Vector3 savedPos = playerData.Position;
                    DebugLog($"Loaded player position from WorldSaveManager for client {clientId}: {savedPos}");
                    return savedPos;
                }
            }

            // Fall back to PlayerPrefs for backward compatibility
            string key = $"{playerPositionKey}_{clientId}";
            
            if (PlayerPrefs.HasKey(key))
            {
                string posData = PlayerPrefs.GetString(key, "");
                
                if (!string.IsNullOrEmpty(posData))
                {
                    string[] components = posData.Split(',');
                    if (components.Length == 3)
                    {
                        float x = float.Parse(components[0]);
                        float y = float.Parse(components[1]);
                        float z = float.Parse(components[2]);
                        return new Vector3(x, y, z);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error loading player position: {e.Message}");
        }
        
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
    
    private IEnumerator PrepareSpawnLocation(ulong clientId, Vector3 initialPosition)
    {
        DebugLog($"Preparing spawn location for client {clientId} at {initialPosition}");
        
        // Wait a short delay to allow things to initialize
        yield return new WaitForSeconds(0.5f);
        
        // First ensure chunks are loaded around the initial position
        if (prioritizeSpawnTerrainLoading)
        {
            EnsureTerrainLoaded(initialPosition);
        }
        
        // Wait some more frames for terrain to generate - INCREASED WAIT TIME
        yield return new WaitForSeconds(2.0f); // Increased from 1.0f to 2.0f
        
        // Find a valid ground position
        Vector3 groundedPosition;
        
        if (clientPositionLoaded.TryGetValue(clientId, out bool isLoaded) && isLoaded)
        {
            // For returning players, just do a simple ground check
            groundedPosition = FindGroundBelow(initialPosition);
            
            // If no ground found, fall back to full search
            if (groundedPosition == Vector3.zero)
            {
                DebugLog($"No ground found below previous position, trying default position for client {clientId}");
                groundedPosition = FindValidSpawnPosition(defaultSpawnPosition);
            }
        }
        else
        {
            // For new players, start at the default position (0,0,0)
            groundedPosition = FindValidSpawnPosition(defaultSpawnPosition);
        }
        
        // If still no valid position, use a default high position
        if (groundedPosition == Vector3.zero)
        {
            DebugLog($"WARNING: No valid ground found, using emergency spawn at height for client {clientId}");
            groundedPosition = new Vector3(initialPosition.x, maxSpawnHeight / 2, initialPosition.z);
        }
        
        // Update the pending position
        pendingSpawnPositions[clientId] = groundedPosition;
        
        // Inform listeners that we found a position
        OnPlayerSpawnPositionFound?.Invoke(clientId, groundedPosition);
        
        DebugLog($"Final spawn position for client {clientId}: {groundedPosition}");
        
        // If we already have a player object, teleport it to the new position
        if (playerObjects.TryGetValue(clientId, out GameObject playerObj) && playerObj != null)
        {
            TeleportPlayer(clientId, playerObj, groundedPosition);
        }
    }

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
    
    public Vector3 GetSpawnPosition(ulong clientId)
    {
        // If there's already a position pending, return it
        if (pendingSpawnPositions.TryGetValue(clientId, out Vector3 position))
        {
            return position;
        }
        
        // If we should load previous position and have one saved
        if (loadPreviousPosition)
        {
            Vector3 savedPosition = GetClientLastPosition(clientId);
            if (savedPosition != Vector3.zero)
            {
                DebugLog($"Using saved position for client {clientId}: {savedPosition}");
                pendingSpawnPositions[clientId] = savedPosition;
                
                // Start looking for ground around this position
                StartCoroutine(PrepareSpawnLocation(clientId, savedPosition));
                
                return savedPosition;
            }
        }
        
        // Otherwise use default spawn position (0,0,0)
        Vector3 initialPos = new Vector3(
            defaultSpawnPosition.x,
            defaultSpawnPosition.y + 10f, // Start a bit above the default position
            defaultSpawnPosition.z
        );
        
        DebugLog($"Using default position for client {clientId}: {initialPos}");
        pendingSpawnPositions[clientId] = initialPos;
        
        // Start looking for ground
        StartCoroutine(PrepareSpawnLocation(clientId, initialPos));
        
        return initialPos;
    }
    
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
        
        DebugLog($"Teleporting player {clientId} to {position}");
        
        // Disable any CharacterController before teleporting
        CharacterController charController = playerObject.GetComponent<CharacterController>();
        if (charController != null)
        {
            charController.enabled = false;
        }
        
        // Set position directly
        playerObject.transform.position = position;
        
        // Re-enable CharacterController
        if (charController != null)
        {
            charController.enabled = true;
        }
        
        // If we have a NetworkTransform, use Teleport() method
        var netTransform = playerObject.GetComponent<Unity.Netcode.Components.NetworkTransform>();
        if (netTransform != null)
        {
            netTransform.Teleport(position, playerObject.transform.rotation, playerObject.transform.localScale);
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