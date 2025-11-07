using System.Collections;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using System;
using NelsUtils;

public class PlayerFallRecovery : NetworkBehaviour
{
    [Header("Fall Detection")]
    [SerializeField] private float minAcceptableHeight = -20f;
    [SerializeField] private float checkInterval = 1f;
    [SerializeField] private int maxSelfRescueAttempts = 3;
    [SerializeField] private int immediateChunkLoadRadius = 2;
    
    [Header("Recovery")]
    [SerializeField] private float upwardOffset = 5f;
    [SerializeField] private LayerMask terrainMask;
    [SerializeField] private int maxPositionHistoryCount = 20;
    [SerializeField] private float rescueCooldownTime = 10f;
    
    // Component References
    private CharacterController characterController;
    private Rigidbody playerRigidbody;
    private NetworkObject networkObject;
    
    // Internal state
    private CircularPositionBuffer validPositionHistory;
    private int rescueAttempts = 0;
    private float lastRescueTime = 0f;
    private Coroutine checkCoroutine;
    private Coroutine gravityRestoreCoroutine;
    private bool isFalling = false;
    private bool isInitialized = false;

    private void Awake()
    {
        // Set terrain layer if not assigned
        if (terrainMask == 0)
        {
            terrainMask = LayerMask.GetMask("Terrain");
        }
        
        // Initialize components
        characterController = GetComponent<CharacterController>();
        playerRigidbody = GetComponent<Rigidbody>();
        networkObject = GetComponent<NetworkObject>();
        
        // Initialize the circular position buffer
        validPositionHistory = new CircularPositionBuffer(maxPositionHistoryCount);
    }
    
    private void Start()
    {
        // Skip for non-owners
        if (!IsOwner) return;
        
        // Wait a short time to let other systems initialize
        StartCoroutine(DelayedStart());
    }
    
    private IEnumerator DelayedStart()
    {
        // Wait a moment for other systems to initialize
        yield return new WaitForSeconds(1.5f);
        
        // Store initial position as valid if it's on solid ground
        if (IsPositionValid(transform.position))
        {
            validPositionHistory.Add(transform.position);
        }
        
        isInitialized = true;
        
        // Start the fall check coroutine
        StartCheckCoroutine();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // Skip for non-owners
        if (!IsOwner) return;
        
        // Additional initialization for network spawn
        StartCheckCoroutine();
    }

    public override void OnNetworkDespawn()
    {
        // Clean up coroutines
        if (checkCoroutine != null)
        {
            StopCoroutine(checkCoroutine);
            checkCoroutine = null;
        }
        
        if (gravityRestoreCoroutine != null)
        {
            StopCoroutine(gravityRestoreCoroutine);
            gravityRestoreCoroutine = null;
        }
        
        base.OnNetworkDespawn();
    }
    
    private void OnDestroy()
    {
        // Clean up coroutines
        if (checkCoroutine != null)
        {
            StopCoroutine(checkCoroutine);
            checkCoroutine = null;
        }
        
        if (gravityRestoreCoroutine != null)
        {
            StopCoroutine(gravityRestoreCoroutine);
            gravityRestoreCoroutine = null;
        }
    }

    private void StartCheckCoroutine()
    {
        // Only for owners
        if (!IsOwner) return;
        
        // Don't start multiple coroutines
        if (checkCoroutine != null)
        {
            StopCoroutine(checkCoroutine);
        }
        
        checkCoroutine = StartCoroutine(CheckFallStatus());
    }

    private IEnumerator CheckFallStatus()
    {
        // Initial delay to let world initialize
        yield return new WaitForSeconds(1f);
        
        while (true)
        {
            yield return new WaitForSeconds(checkInterval);
            
            // Skip if we're not fully initialized
            if (!isInitialized) continue;
            
            // Record valid positions when player is on solid ground
            if (IsPositionValid(transform.position) && transform.position.y > minAcceptableHeight)
            {
                validPositionHistory.Add(transform.position);
                isFalling = false;
            }
            
            // Check if we've fallen below the acceptable limit
            if (transform.position.y < minAcceptableHeight && !isFalling)
            {
                isFalling = true;
                
                // Check cooldown to prevent rapid rescues
                if (Time.time - lastRescueTime > rescueCooldownTime && rescueAttempts < maxSelfRescueAttempts)
                {
                    RequestRescueServerRpc();
                    lastRescueTime = Time.time;
                    rescueAttempts++;
                }
                else if (rescueAttempts >= maxSelfRescueAttempts)
                {
                    // If we've tried too many self-rescues, ask the server for help
                    RequestServerRescueServerRpc();
                    lastRescueTime = Time.time;
                    rescueAttempts = 0; // Reset counter after server rescue
                }
            }
        }
    }
    
    private bool IsPositionValid(Vector3 position)
    {
        // Cast rays in multiple directions to ensure stability
        Vector3 rayStart = position + Vector3.up * 0.5f;
        
        // Check downward
        if (Physics.Raycast(rayStart, Vector3.down, 2f, terrainMask))
        {
            return true;
        }
        
        // If we're very close to the ground, check multiple angles
        for (float angle = 0; angle < 360f; angle += 60f)
        {
            Vector3 direction = Quaternion.Euler(0, angle, 0) * Vector3.forward;
            direction = Vector3.down + direction.normalized * 0.5f;
            
            if (Physics.Raycast(rayStart, direction.normalized, 1.5f, terrainMask))
            {
                return true;
            }
        }
        
        return false;
    }

    [ServerRpc]
    private void RequestRescueServerRpc(ServerRpcParams rpcParams = default)
    {
        // Get client ID from RPC params
        ulong clientId = rpcParams.Receive.SenderClientId;
        
        Debug.Log($"Player {clientId} has requested rescue from falling!");
        
        // Try to find a valid position to rescue the player
        Vector3 rescuePosition = FindRescuePosition(clientId);
        
        // Execute the rescue
        PerformRescueClientRpc(rescuePosition);
        
        // Also teleport directly on the server (for the server version of the player)
        TeleportPlayer(rescuePosition);
        
        // Force load chunks around the rescue position
        ForceLoadSurroundingChunks(rescuePosition);
        
        Debug.Log($"Rescued player {clientId} to position {rescuePosition}. Attempt #{rescueAttempts}");
    }
    
    [ServerRpc]
    private void RequestServerRescueServerRpc(ServerRpcParams rpcParams = default)
    {
        // This is a more forceful rescue attempt for when the player has fallen too many times
        ulong clientId = rpcParams.Receive.SenderClientId;
        
        Debug.Log($"Server rescue requested for player {clientId} - multiple fall detections");
        
        // Find a completely fresh spawn position
        Vector3 rescuePosition;
        
        if (PlayerSpawner.Instance != null)
        {
            // Request a completely new spawn position
            rescuePosition = PlayerSpawner.Instance.GetSpawnPosition(clientId);
            Debug.Log($"Using PlayerSpawner to get fresh spawn position: {rescuePosition}");
        }
        else
        {
            // Fallback: Find a high position above current XZ
            rescuePosition = new Vector3(transform.position.x, 100f, transform.position.z);
            
            // Try to find ground below this position
            if (Physics.Raycast(rescuePosition, Vector3.down, out RaycastHit hit, 150f, terrainMask))
            {
                rescuePosition = hit.point + (Vector3.up * upwardOffset);
            }
            Debug.Log($"Using fallback high position for rescue: {rescuePosition}");
        }
        
        // Perform the rescue
        PerformRescueClientRpc(rescuePosition);
        TeleportPlayer(rescuePosition);
        
        // Force load chunks around the rescue position
        ForceLoadSurroundingChunks(rescuePosition);
        
        // Log the event
        Debug.Log($"SERVER RESCUE: Player {clientId} moved to fresh position {rescuePosition}");
    }
    
    private Vector3 FindRescuePosition(ulong clientId)
    {
        // STRATEGY 1: Try to get a valid position from the history buffer
        Vector3 lastValidPosition = validPositionHistory.GetMostRecent();
        if (lastValidPosition != Vector3.zero)
        {
            Debug.Log($"Using position from history buffer: {lastValidPosition}");
            return lastValidPosition;
        }
        
        // STRATEGY 2: Try to use the player spawner
        if (PlayerSpawner.Instance != null)
        {
            Vector3 spawnPosition = PlayerSpawner.Instance.FindGroundForPlayer(clientId, transform.position);
            Debug.Log($"Using PlayerSpawner to find ground: {spawnPosition}");
            return spawnPosition;
        }
        
        // STRATEGY 3: Cast rays to find nearby terrain
        Vector3 currentPos = transform.position;
        Vector3 foundPos = FindGroundNear(currentPos);
        if (foundPos != Vector3.zero)
        {
            Debug.Log($"Found ground near current position: {foundPos}");
            return foundPos;
        }
        
        // STRATEGY 4: Go up and cast rays in a spiral pattern
        Vector3 highPos = new Vector3(currentPos.x, currentPos.y + 100f, currentPos.z);
        foundPos = FindGroundInSpiral(highPos, 5, 10f);
        if (foundPos != Vector3.zero)
        {
            Debug.Log($"Found ground using spiral search: {foundPos}");
            return foundPos;
        }
        
        // STRATEGY 5: Last resort - just go up a lot
        Debug.Log($"Using last resort position above current XZ coordinates");
        return new Vector3(currentPos.x, 100f, currentPos.z);
    }
    
    private Vector3 FindGroundNear(Vector3 position)
    {
        // Try to find ground below the position
        if (Physics.Raycast(position + Vector3.up * 50f, Vector3.down, out RaycastHit hit, 150f, terrainMask))
        {
            return hit.point + Vector3.up * upwardOffset;
        }
        
        return Vector3.zero;
    }
    
    private Vector3 FindGroundInSpiral(Vector3 center, int rings, float spacing)
    {
        // Search in expanding spiral
        for (int ring = 0; ring <= rings; ring++)
        {
            int segments = ring * 8;
            if (segments < 8) segments = 8; // Minimum 8 segments for the first ring
            
            for (int segment = 0; segment < segments; segment++)
            {
                float angle = (segment / (float)segments) * Mathf.PI * 2f;
                float radius = ring * spacing;
                
                Vector3 offset = new Vector3(
                    Mathf.Cos(angle) * radius,
                    0f,
                    Mathf.Sin(angle) * radius
                );
                
                Vector3 testPoint = center + offset;
                
                // Try to find ground below this point
                if (Physics.Raycast(testPoint, Vector3.down, out RaycastHit hit, 150f, terrainMask))
                {
                    return hit.point + Vector3.up * upwardOffset;
                }
            }
        }
        
        return Vector3.zero;
    }
    
    [ClientRpc]
    private void PerformRescueClientRpc(Vector3 rescuePosition, ClientRpcParams clientRpcParams = default)
    {
        // Teleport on all clients
        TeleportPlayer(rescuePosition);
        
        if (IsOwner)
        {
            Debug.Log($"Local player rescued to {rescuePosition}");
            lastRescueTime = Time.time;
            
            // Store the rescue position in the history buffer
            validPositionHistory.Clear(); // Clear history since we're at a new location
            validPositionHistory.Add(rescuePosition);
            
            // Temporarily disable gravity for stability until we confirm ground
            DisableGravityTemporarily(3f);
            
            // Clear the falling state
            isFalling = false;
        }
    }
    
    private void DisableGravityTemporarily(float duration)
    {
        // Cancel previous coroutine if it exists
        if (gravityRestoreCoroutine != null)
        {
            StopCoroutine(gravityRestoreCoroutine);
        }
        
        // Disable gravity depending on the character controller type
        if (characterController != null)
        {
            // Gravity is typically applied manually in character controllers
            // Add a component to signal to the controller to ignore gravity
            // or simply let the controller know to temporarily ignore gravity
            TemporaryGravitySuspension suspension = gameObject.GetComponent<TemporaryGravitySuspension>();
            if (suspension == null)
            {
                suspension = gameObject.AddComponent<TemporaryGravitySuspension>();
            }
            suspension.SuspendGravity(duration);
        }
        else if (playerRigidbody != null)
        {
            // If using Rigidbody, disable gravity directly
            playerRigidbody.useGravity = false;
        }
        
        // Start the coroutine to restore gravity after the duration
        gravityRestoreCoroutine = StartCoroutine(RestoreGravity(duration));
    }
    
    private IEnumerator RestoreGravity(float delay)
    {
        yield return new WaitForSeconds(delay);
        
        // Wait until we've verified there's ground beneath us
        int maxAttempts = 10;
        int attempts = 0;
        bool groundFound = false;
        
        while (!groundFound && attempts < maxAttempts)
        {
            groundFound = IsPositionValid(transform.position);
            
            if (!groundFound)
            {
                yield return new WaitForSeconds(0.5f);
                attempts++;
            }
        }
        
        // Restore gravity
        if (playerRigidbody != null)
        {
            playerRigidbody.useGravity = true;
        }
        
        // The TemporaryGravitySuspension will handle itself if it exists
        
        gravityRestoreCoroutine = null;
    }

    private void ForceLoadSurroundingChunks(Vector3 position)
    {
        if (!IsServer) return;
        
        if (World.Instance != null)
        {
            // Force update player position in World to trigger chunk loading
            World.Instance.UpdatePlayerPosition(position);
            
            // Get the player's current chunk coordinates
            Vector3Int chunkCoord = Coord.WorldToChunkCoord(
                position, 
                World.Instance.chunkSize, 
                World.Instance.voxelSize
            );
            
            // Prioritize loading chunks immediately around player
            if (ChunkOperationsQueue.Instance != null)
            {
                for (int x = -immediateChunkLoadRadius; x <= immediateChunkLoadRadius; x++)
                for (int y = -immediateChunkLoadRadius; y <= immediateChunkLoadRadius; y++)
                for (int z = -immediateChunkLoadRadius; z <= immediateChunkLoadRadius; z++)
                {
                    Vector3Int neighborCoord = chunkCoord + new Vector3Int(x, y, z);
                    
                    // Immediate chunk loading, disable quick check to ensure proper terrain generation
                    ChunkOperationsQueue.Instance.QueueChunkForLoad(neighborCoord, true, false);
                    Debug.Log($"Forcing load of chunk {neighborCoord} for player rescue");
                }
            }
        }
        else
        {
            Debug.LogWarning("World.Instance not available for forced chunk loading during rescue");
        }
    }
    
    private void TeleportPlayer(Vector3 position)
    {
        // Handle CharacterController if present
        if (characterController != null)
        {
            characterController.enabled = false;
        }
        
        // Set position directly
        transform.position = position;
        
        // Re-enable controller
        if (characterController != null)
        {
            characterController.enabled = true;
        }
        
        // Update position on NetworkTransform if present
        NetworkTransform netTransform = GetComponent<NetworkTransform>();
        if (netTransform != null)
        {
            netTransform.Teleport(position, transform.rotation, transform.localScale);
        }
        
        // Try to find and use NetworkPlayer.TeleportClientRpc if available
        NetworkPlayer networkPlayer = GetComponent<NetworkPlayer>();
        if (networkPlayer != null)
        {
            try
            {
                var method = networkPlayer.GetType().GetMethod("TeleportClientRpc");
                if (method != null)
                {
                    method.Invoke(networkPlayer, new object[] { position });
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error invoking TeleportClientRpc: {e.Message}");
            }
        }
    }
    
    private class CircularPositionBuffer
    {
        private Vector3[] positions;
        private int currentIndex = -1;
        private int count = 0;
        
        public CircularPositionBuffer(int capacity)
        {
            positions = new Vector3[capacity];
        }
        
        public void Add(Vector3 position)
        {
            currentIndex = (currentIndex + 1) % positions.Length;
            positions[currentIndex] = position;
            count = Mathf.Min(count + 1, positions.Length);
        }
        
        public Vector3 GetMostRecent()
        {
            if (count == 0) return Vector3.zero;
            return positions[currentIndex];
        }
        
        public void Clear()
        {
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = Vector3.zero;
            }
            currentIndex = -1;
            count = 0;
        }
    }
}


public class TemporaryGravitySuspension : MonoBehaviour
{
    public bool ShouldIgnoreGravity { get; private set; } = false;
    private float suspensionEndTime = 0f;
    
    private void Update()
    {
        if (ShouldIgnoreGravity && Time.time >= suspensionEndTime)
        {
            ShouldIgnoreGravity = false;
            Destroy(this);
        }
    }
    
    public void SuspendGravity(float duration)
    {
        ShouldIgnoreGravity = true;
        suspensionEndTime = Time.time + duration;
    }
}