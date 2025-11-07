using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using System;
using System.Linq;
using Unity.Netcode.Components;

/// <summary>
/// Helper class for NetworkManager integration with terrain and player management
/// This class should be attached to the same GameObject as NetworkManager
/// </summary>
public class NetworkManagerHelper : MonoBehaviour
{
    [Header("Player Integration")]
    [SerializeField] private bool usePlayerSpawner = true;
    [SerializeField] private float initialSetupDelay = 0.5f;
    [SerializeField] private float playerPositionUpdateInterval = 2f;
    
    [Header("Terrain Integration")]
    [SerializeField] private bool ensureTerrainManagerExists = true;
    [SerializeField] private GameObject networkTerrainManagerPrefab;

    private NetworkManager networkManager;
    private Dictionary<ulong, GameObject> trackedPlayers = new Dictionary<ulong, GameObject>();
    private Dictionary<ulong, Vector3> playerPositions = new Dictionary<ulong, Vector3>();
    private bool isInitialized = false;
    private float lastPositionUpdateTime = 0f;

    private void Awake()
    {
        Debug.Log("[NetworkManagerHelper] Initializing");
        
        networkManager = GetComponent<NetworkManager>();
        if (networkManager == null)
        {
            Debug.LogError("NetworkManagerHelper must be on same object as NetworkManager!");
            enabled = false;
            return;
        }
    }

    private void Start()
    {
        // Subscribe to events
        if (networkManager != null)
        {
            networkManager.OnServerStarted += OnServerStarted;
            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnect;
            
            isInitialized = true;
            Debug.Log("[NetworkManagerHelper] Initialized successfully");
        }
    }

    private void Update()
    {
        // Only run on server and at regular intervals
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && 
            Time.time - lastPositionUpdateTime > playerPositionUpdateInterval)
        {
            UpdateAllPlayerPositions();
            lastPositionUpdateTime = Time.time;
        }
    }

    private void OnDestroy()
    {
        if (networkManager != null)
        {
            networkManager.OnServerStarted -= OnServerStarted;
            networkManager.OnClientConnectedCallback -= OnClientConnected;
            networkManager.OnClientDisconnectCallback -= OnClientDisconnect;
        }
        
        trackedPlayers.Clear();
        playerPositions.Clear();
    }

    public bool IsServerOnlyMode()
    {
        return NetworkManager.Singleton != null && 
            NetworkManager.Singleton.IsServer && 
            !NetworkManager.Singleton.IsHost;
    }

    private void OnServerStarted()
    {
        Debug.Log("[NetworkManagerHelper] Server started");
        
        // Ensure NetworkTerrainManager exists
        if (ensureTerrainManagerExists && networkTerrainManagerPrefab != null)
        {
            EnsureTerrainManagerExists();
        }
        
        // Special handling for server-only mode
        if (IsServerOnlyMode())
        {
            Debug.Log("[NetworkManagerHelper] Server-only mode detected - initializing without player");
            
            // Initialize PlayerSpawner with server mode if available
            if (PlayerSpawner.Instance != null)
            {
                PlayerSpawner.Instance.InitializeServerMode();
            }
            
            // Initialize World with server mode if needed
            if (World.Instance != null)
            {
                // Set a default position for terrain generation
                World.Instance.UpdatePlayerPosition(Vector3.zero);
            }
        }
    }

    private void EnsureTerrainManagerExists()
    {
        // Only run on server
        if (!NetworkManager.Singleton.IsServer)
            return;
            
        // Check if it already exists using the singleton instance
        if (NetworkTerrainManager.Instance != null)
        {
            Debug.Log("[NetworkManagerHelper] NetworkTerrainManager already exists");
            return;
        }
        
        // Create a new instance
        try
        {
            var terrainManager = Instantiate(networkTerrainManagerPrefab);
            var netObj = terrainManager.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                netObj.Spawn();
                Debug.Log("[NetworkManagerHelper] NetworkTerrainManager spawned on server");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[NetworkManagerHelper] Failed to spawn NetworkTerrainManager: {e.Message}");
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!networkManager.IsServer) return;

        Debug.Log($"[NetworkManagerHelper] Client {clientId} connected");
        
        // Skip player setup for server client ID in server-only mode
        if (IsServerOnlyMode() && clientId == NetworkManager.ServerClientId)
        {
            Debug.Log($"[NetworkManagerHelper] Skipping player setup for server client ID in server-only mode");
            return;
        }
        
        // Wait a moment for the player object to be fully set up
        StartCoroutine(DelayedPlayerSetup(clientId));
    }

    private System.Collections.IEnumerator DelayedPlayerSetup(ulong clientId)
    {
        yield return new WaitForSeconds(initialSetupDelay);
        
        // Find player object
        GameObject playerObject = FindPlayerObject(clientId);
        if (playerObject != null)
        {
            trackedPlayers[clientId] = playerObject;
            playerPositions[clientId] = playerObject.transform.position;
            
            // Register player with WorldSaveManager
            if (WorldSaveManager.Instance != null && PlayerSpawner.Instance != null)
            {
                PlayerSpawner.Instance.RegisterPlayerWithWorld(clientId, playerObject);
                Debug.Log($"[NetworkManagerHelper] Registered player {clientId} with WorldSaveManager");
            }
            
            // Update World with this player's position
            if (World.Instance != null)
            {
                World.Instance.RegisterPlayerPosition(clientId, playerObject.transform.position);
                Debug.Log($"[NetworkManagerHelper] Registered player {clientId} position with World");
            }
        }
        else
        {
            Debug.LogWarning($"[NetworkManagerHelper] Player object not found for client {clientId}");
        }
    }

    private void OnClientDisconnect(ulong clientId)
    {
        // Save player data before removing
        if (trackedPlayers.TryGetValue(clientId, out GameObject playerObject) && 
            playerObject != null && WorldSaveManager.Instance != null)
        {
            // Save player position
            if (PlayerSpawner.Instance != null)
            {
                PlayerSpawner.Instance.SavePlayerPosition(clientId, playerObject.transform.position);
                Debug.Log($"[NetworkManagerHelper] Saved final position for disconnecting player {clientId}");
            }
        }
        
        // Clean up resources
        trackedPlayers.Remove(clientId);
        playerPositions.Remove(clientId);
        
        // Unregister player from World
        if (World.Instance != null)
        {
            World.Instance.UnregisterPlayer(clientId);
            Debug.Log($"[NetworkManagerHelper] Unregistered player {clientId} from World");
        }
        
        Debug.Log($"[NetworkManagerHelper] Client {clientId} disconnected, resources cleaned up");
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

    private void UpdateAllPlayerPositions()
    {
        // Only server needs to track all positions
        if (!NetworkManager.Singleton.IsServer)
            return;
            
        // Refresh all player positions
        List<ulong> updatedClients = new List<ulong>();
        
        foreach (var entry in trackedPlayers)
        {
            ulong clientId = entry.Key;
            GameObject playerObj = entry.Value;
            
            if (playerObj != null)
            {
                Vector3 currentPos = playerObj.transform.position;
                
                // Check if we've moved significantly
                if (!playerPositions.TryGetValue(clientId, out Vector3 lastPos) || 
                    Vector3.Distance(currentPos, lastPos) > 1.0f)
                {
                    // Update stored position
                    playerPositions[clientId] = currentPos;
                    updatedClients.Add(clientId);
                    
                    // Update World system with new player position
                    if (World.Instance != null)
                    {
                        World.Instance.UpdatePlayerPositionForClient(clientId, currentPos);
                        
                        // Only log occasionally to reduce spam
                        if (Time.frameCount % 300 == 0)
                        {
                            Debug.Log($"[NetworkManagerHelper] Updated position for player {clientId}: {currentPos}");
                        }
                    }
                }
            }
        }
        
        // Only trigger global updates periodically to avoid overloading the system
        if (updatedClients.Count > 0 && World.Instance != null && Time.frameCount % 60 == 0)
        {
            World.Instance.UpdateGlobalChunkState();
        }
    }
    
    // Get a list of all current player positions
    public Dictionary<ulong, Vector3> GetAllPlayerPositions()
    {
        // Return a copy to avoid modification from outside
        return new Dictionary<ulong, Vector3>(playerPositions);
    }
}