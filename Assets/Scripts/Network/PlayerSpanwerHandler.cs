using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;

/// <summary>
/// Handles integration between NetworkManager and the ImprovedPlayerSpawner
/// This component should be attached to the NetworkManager GameObject
/// </summary>
public class PlayerSpawnHandler : MonoBehaviour
{
    [Header("Spawn Settings")]
    [SerializeField] private bool waitForTerrainLoad = true;
    [SerializeField] private float spawnDelay = 0.5f;
    [SerializeField] private bool enablePlayerHeightCheck = true;
    [SerializeField] private float maxFallDistance = 100f;
    [SerializeField] private LayerMask terrainLayerMask;
    [SerializeField] private bool autoRegisterApprovalCallback = true;
    
    // Internal state
    private Dictionary<ulong, NetworkObject> pendingPlayers = new Dictionary<ulong, NetworkObject>();
    private Dictionary<ulong, bool> playerSpawnCompleted = new Dictionary<ulong, bool>();
    private Dictionary<ulong, Vector3> spawnPositions = new Dictionary<ulong, Vector3>();
    private bool isApprovalCallbackRegistered = false;
    
    private void Start()
    {
        // Set up terrain layer if not assigned
        if (terrainLayerMask == 0)
        {
            terrainLayerMask = LayerMask.GetMask("Terrain");
        }
        
        // Subscribe to events
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            
            // Only register approval callback if we're supposed to and it's not already registered
            TryRegisterApprovalCallback();
        }
        
        // Connect to ImprovedPlayerSpawner events
        if (PlayerSpawner.Instance != null)
        {
            PlayerSpawner.Instance.OnPlayerSpawnPositionFound += OnSpawnPositionFound;
        }
        else
        {
            Debug.LogWarning("ImprovedPlayerSpawner instance not found - player spawning might not work correctly");
        }
    }

    private void TryRegisterApprovalCallback()
    {
        if (!autoRegisterApprovalCallback || isApprovalCallbackRegistered) 
            return;
            
        try {
            // Check if PlayerSpawner already registered a callback
            if (PlayerSpawner.Instance != null && 
                typeof(PlayerSpawner).GetMethod("HandleConnectionApproval") != null)
            {
                Debug.Log("PlayerSpawnHandler: PlayerSpawner appears to handle connection approval, skipping registration");
                return;
            }
            
            NetworkManager.Singleton.ConnectionApprovalCallback += ConnectionApproval;
            isApprovalCallbackRegistered = true;
            Debug.Log("PlayerSpawnHandler: Successfully registered connection approval callback");
        }
        catch (System.InvalidOperationException ex) {
            Debug.LogWarning($"PlayerSpawnHandler: Approval callback already registered elsewhere: {ex.Message}");
        }
        catch (System.Exception ex) {
            Debug.LogError($"PlayerSpawnHandler: Error registering approval callback: {ex.Message}");
        }
    }
    
    private void OnDestroy()
    {
        // Unsubscribe from events
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            
            if (isApprovalCallbackRegistered)
            {
                NetworkManager.Singleton.ConnectionApprovalCallback -= ConnectionApproval;
            }
        }
        
        if (PlayerSpawner.Instance != null)
        {
            PlayerSpawner.Instance.OnPlayerSpawnPositionFound -= OnSpawnPositionFound;
        }
    }
    
    private void ConnectionApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // Always approve connections
        response.Approved = true;
        response.CreatePlayerObject = true;
        
        // Use a default position for initial spawn - we'll reposition the player later
        Vector3 initialPosition = Vector3.zero;
        
        // Try to get position from the spawner if available
        if (PlayerSpawner.Instance != null)
        {
            initialPosition = PlayerSpawner.Instance.GetSpawnPosition(request.ClientNetworkId);
        }
        
        // Assign spawn position
        response.Position = initialPosition;
        response.Rotation = Quaternion.identity;
        
        // Cache the expected spawn position
        spawnPositions[request.ClientNetworkId] = initialPosition;
        
        Debug.Log($"Approved client {request.ClientNetworkId} with initial position: {initialPosition}");
    }
    
    private void OnClientConnected(ulong clientId)
    {
        if (!NetworkManager.Singleton.IsServer)
            return;
            
        Debug.Log($"Client {clientId} connected");
        
        // Track player spawn completion
        playerSpawnCompleted[clientId] = false;
        
        // Find the player object
        StartCoroutine(FindAndSetupPlayerObject(clientId));
    }

    private IEnumerator FindAndSetupPlayerObject(ulong clientId)
    {
        NetworkObject playerObject = null;
        float searchTime = 0f;
        float maxSearchTime = 5f;

        // Search for player object with increasing wait time
        while (playerObject == null && searchTime < maxSearchTime)
        {
            foreach (NetworkObject netObj in NetworkManager.Singleton.SpawnManager.SpawnedObjects.Values)
            {
                if (netObj.IsPlayerObject && netObj.OwnerClientId == clientId)
                {
                    playerObject = netObj;
                    pendingPlayers[clientId] = netObj;
                    break;
                }
            }

            if (playerObject == null)
            {
                searchTime += 0.2f;
                yield return new WaitForSeconds(0.2f);
            }
        }

        if (playerObject == null)
        {
            Debug.LogWarning($"Failed to find player object for client {clientId} after {maxSearchTime} seconds");
            yield break;
        }

        // If we're not waiting for terrain, position player immediately
        if (!waitForTerrainLoad)
        {
            FinalizePlayerSpawn(clientId, playerObject);
        }
        // Otherwise wait for ImprovedPlayerSpawner to find a valid position
        else if (spawnPositions.TryGetValue(clientId, out Vector3 position))
        {
            // Start checking if the terrain is loaded
            StartCoroutine(WaitForTerrainLoad(clientId, playerObject, position));
        }
    }
    
    private void OnSpawnPositionFound(ulong clientId, Vector3 position)
    {
        Debug.Log($"Spawn position found for client {clientId}: {position}");
        spawnPositions[clientId] = position;
        
        // If we have the player object, update its position
        if (pendingPlayers.TryGetValue(clientId, out NetworkObject playerObj))
        {
            bool alreadySpawned = playerSpawnCompleted.TryGetValue(clientId, out bool completed) && completed;
            
            if (alreadySpawned)
            {
                // Player already spawned, just teleport to new position
                TeleportPlayer(clientId, playerObj, position);
            }
            else
            {
                // Finalize the spawn with the found position
                FinalizePlayerSpawn(clientId, playerObj);
            }
        }
    }
    
    private IEnumerator WaitForTerrainLoad(ulong clientId, NetworkObject playerObj, Vector3 position)
    {
        Debug.Log($"Waiting for terrain to load for client {clientId}");
        
        // Wait a bit to let terrain load
        yield return new WaitForSeconds(spawnDelay);
        
        // See if the ImprovedPlayerSpawner has found a better position
        if (spawnPositions.TryGetValue(clientId, out Vector3 updatedPosition) && 
            updatedPosition != position)
        {
            position = updatedPosition;
        }
        
        // Check if there's terrain underneath
        bool terrainLoaded = CheckTerrainLoaded(position);
        
        int attempts = 0;
        const int maxAttempts = 10;
        
        while (!terrainLoaded && attempts < maxAttempts)
        {
            yield return new WaitForSeconds(0.5f);
            terrainLoaded = CheckTerrainLoaded(position);
            attempts++;
            
            // Check if position was updated while waiting
            if (spawnPositions.TryGetValue(clientId, out updatedPosition) && 
                updatedPosition != position)
            {
                position = updatedPosition;
                terrainLoaded = CheckTerrainLoaded(position);
            }
        }
        
        Debug.Log($"Terrain loaded for client {clientId}: {terrainLoaded}");
        
        // Finalize the spawn now
        FinalizePlayerSpawn(clientId, playerObj);
    }
    
    private bool CheckTerrainLoaded(Vector3 position)
    {
        // Check if there's terrain below by raycasting downward
        Ray ray = new Ray(position + Vector3.up * 10f, Vector3.down);
        return Physics.Raycast(ray, maxFallDistance, terrainLayerMask);
    }
    
    private void FinalizePlayerSpawn(ulong clientId, NetworkObject playerObj)
    {
        if (playerObj == null || !playerObj.IsSpawned)
        {
            Debug.LogWarning($"Player object for client {clientId} is no longer valid");
            return;
        }
        
        // Get the best spawn position we have
        Vector3 position = Vector3.zero;
        if (spawnPositions.TryGetValue(clientId, out Vector3 storedPosition))
        {
            position = storedPosition;
        }
        
        // Teleport the player
        TeleportPlayer(clientId, playerObj, position);
        
        // Mark as completed
        playerSpawnCompleted[clientId] = true;
        
        // Register with the spawner
        if (PlayerSpawner.Instance != null)
        {
            PlayerSpawner.Instance.RegisterPlayerSpawn(clientId, position);
        }
        
        // Start fall safety check
        if (enablePlayerHeightCheck)
        {
            StartCoroutine(CheckPlayerHeight(clientId, playerObj));
        }
        
        Debug.Log($"Finalized spawn for client {clientId} at position {position}");
    }
    
    private void TeleportPlayer(ulong clientId, NetworkObject playerObj, Vector3 position)
    {
        // Disable any CharacterController before teleporting
        CharacterController charController = playerObj.GetComponent<CharacterController>();
        if (charController != null)
        {
            charController.enabled = false;
        }
        
        // Set position directly
        playerObj.transform.position = position;
        
        // Re-enable CharacterController
        if (charController != null)
        {
            charController.enabled = true;
        }
        
        // Also send position via NetworkTransform if available
        NetworkTransform netTransform = playerObj.GetComponent<NetworkTransform>();
        if (netTransform != null)
        {
            netTransform.Teleport(position, Quaternion.identity, playerObj.transform.localScale);
        }
        
        // If there's a NetworkPlayer component with a custom teleport method, use it
        var networkPlayer = playerObj.GetComponent<NetworkPlayer>();
        if (networkPlayer != null)
        {
            // Assume NetworkPlayer has a TeleportClientRpc method
            try
            {
                var teleportMethod = networkPlayer.GetType().GetMethod("TeleportClientRpc");
                if (teleportMethod != null)
                {
                    teleportMethod.Invoke(networkPlayer, new object[] { position });
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error calling TeleportClientRpc: {e.Message}");
            }
        }
    }
    
    private IEnumerator CheckPlayerHeight(ulong clientId, NetworkObject playerObj)
    {
        const float checkInterval = 2.0f;
        const int maxChecks = 10;
        int checks = 0;
        
        while (playerObj != null && playerObj.IsSpawned && checks < maxChecks)
        {
            yield return new WaitForSeconds(checkInterval);
            
            // Check if player has fallen too far
            if (playerObj.transform.position.y < -20f)
            {
                Debug.Log($"Player {clientId} fell too far, respawning");
                
                // Get a new spawn position
                Vector3 newPosition = Vector3.zero;
                if (PlayerSpawner.Instance != null)
                {
                    newPosition = PlayerSpawner.Instance.GetSpawnPosition(clientId);
                }
                else if (spawnPositions.TryGetValue(clientId, out Vector3 lastPosition))
                {
                    newPosition = new Vector3(lastPosition.x, 50f, lastPosition.z);
                }
                
                // Teleport player back up
                TeleportPlayer(clientId, playerObj, newPosition);
            }
            
            checks++;
        }
    }
}