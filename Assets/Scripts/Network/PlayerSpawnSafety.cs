using System.Collections;
using UnityEngine;
using Unity.Netcode;
using ControllerAssets;
using Unity.Netcode.Components;
using NelsUtils;

/// <summary>
/// Handles safe player spawning by ensuring terrain is loaded before enabling movement.
/// Coordinates with camera setup and has better timeout handling for client-side spawning.
/// Attach to player prefab alongside ThirdPersonController.
/// </summary>
public class PlayerSpawnSafety : NetworkBehaviour
{
    [Header("Safety Settings")]
    [SerializeField] private float maxTerrainWaitTime = 10f;
    [SerializeField] private float groundCheckInterval = 0.5f;
    [SerializeField] private float minHeightAboveGround = 2f;
    [SerializeField] private float maxGroundCheckDistance = 150f;
    [SerializeField] private LayerMask terrainLayer;
    [SerializeField] private bool debugMode = true; // Default to true for debugging

    [Header("Camera Coordination")]
    [SerializeField] private float cameraSetupWaitTime = 2f; // Time to wait for camera setup
    [SerializeField] private float maxControlDisableTime = 15f; // Maximum time controls can be disabled

    [Header("Hover Settings")]
    [SerializeField] private float hoverHeight = 30f;
    [SerializeField] private bool allowHoverMovement = true; // Allow movement while hovering

    [Header("Force Load Options")]
    [SerializeField] private int forceLoadDistance = 2;
    [SerializeField] private bool disableQuickCheck = true; // Disable quick check for spawning chunks

    // Component references
    private ThirdPersonController playerController;
    private CharacterController characterController;
    private Rigidbody playerRigidbody;
    private NetworkObject networkObject;
    private TemporaryGravitySuspension gravitySuspension;
    private NetworkPlayerCameraController cameraController;

    // Internal state
    private bool isInitialized = false;
    private bool isGroundFound = false;
    private bool hasEnabledControls = false;
    private Vector3 initialPosition;
    private float safetyStartTime;
    private float controlDisableStartTime;
    private bool isFallbackPositionUsed = false;
    
    private void Awake()
    {
        // Find required components
        playerController = GetComponent<ThirdPersonController>();
        characterController = GetComponent<CharacterController>();
        playerRigidbody = GetComponent<Rigidbody>();
        networkObject = GetComponent<NetworkObject>();
        cameraController = GetComponent<NetworkPlayerCameraController>();
        
        // Set terrain layer if not assigned
        if (terrainLayer == 0)
        {
            terrainLayer = LayerMask.GetMask("Terrain");
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // Only run on owner client or on server for server-owned objects
        if (!IsOwner && !IsServer) return;
        
        initialPosition = transform.position;
        safetyStartTime = Time.time;
        controlDisableStartTime = Time.time;
        
        // Initialize with suspended gravity
        SuspendGravity();
        
        // Disable player controls initially, but record the time we do so
        if (IsOwner && playerController != null)
        {
            playerController.enabled = false;
            DebugLog($"Player controls disabled during spawn safety checks at t={Time.time}");
        }
        
        // Start safety coroutine
        StartCoroutine(EnsureSafeSpawn());
        
        // Start a backup timer to ensure controls don't stay disabled too long
        StartCoroutine(SafetyTimeoutCheck());
    }

    private IEnumerator SafetyTimeoutCheck()
    {
        // Wait for maximum allowed disable time
        yield return new WaitForSeconds(maxControlDisableTime);
        
        // If controls are still not enabled, force enable them
        if (IsOwner && playerController != null && !playerController.enabled && !hasEnabledControls)
        {
            DebugLog($"WARNING: Forcing player controls on after timeout at t={Time.time}");
            playerController.enabled = true;
            hasEnabledControls = true;
            
            // Try one more time to find a safe position
            Vector3 safePosition = FindSafePosition();
            if (safePosition != Vector3.zero)
            {
                TeleportTo(safePosition);
            }
            else
            {
                // Absolute last resort - try to get a position from PlayerSpawner
                TryFallbackSpawnerPosition();
            }
        }
    }

    private IEnumerator EnsureSafeSpawn()
    {
        DebugLog($"Starting spawn safety for player at {initialPosition}");
        
        // First force load chunks around the spawn position to ensure we have terrain
        ForceLoadSurroundingChunks(initialPosition);
        
        // First wait a bit for camera setup to complete if needed
        if (cameraController != null)
        {
            float waitTime = 0f;
            float maxWait = cameraSetupWaitTime;
            
            DebugLog($"Waiting for camera setup to complete (max {maxWait}s)");
            
            while (waitTime < maxWait && !cameraController.IsSetupComplete())
            {
                waitTime += 0.1f;
                yield return new WaitForSeconds(0.1f);
            }
            
            DebugLog($"Camera wait complete after {waitTime}s. Setup complete: {cameraController.IsSetupComplete()}");
        }
        
        // Lift player up initially if no ground is detected
        if (!IsGroundBelow(initialPosition))
        {
            Vector3 hoverPosition = new Vector3(initialPosition.x, initialPosition.y + hoverHeight, initialPosition.z);
            TeleportTo(hoverPosition);
            DebugLog($"Lifted player to hover position {hoverPosition}");
        }
        
        // Force priority terrain load to ensure chunks are loaded
        ForcePriorityTerrainLoad();
        
        // Wait for a moment to let chunks load
        yield return new WaitForSeconds(1.0f);
        
        // Wait for terrain to load and find ground
        yield return StartCoroutine(WaitForTerrainAndGround());
        
        // If we couldn't find ground after waiting, try multiple fallback methods
        if (!isGroundFound)
        {
            DebugLog("No ground found after waiting, trying fallback methods");
            
            // Try method 1: Use PlayerSpawner as fallback
            Vector3 fallbackPosition = FindFallbackPosition();
            if (fallbackPosition != Vector3.zero)
            {
                TeleportTo(fallbackPosition);
                isGroundFound = true;
                DebugLog($"Using fallback position from PlayerSpawner: {fallbackPosition}");
                yield return new WaitForSeconds(0.5f);
            }
            else
            {
                // Try method 2: Trace ground in spiral pattern
                DebugLog("Trying spiral pattern ground search");
                bool groundFound = FindGroundInSpiral(transform.position + Vector3.up * 50f, out Vector3 spiralPosition);
                if (groundFound)
                {
                    TeleportTo(spiralPosition);
                    isGroundFound = true;
                    DebugLog($"Found ground using spiral search: {spiralPosition}");
                    yield return new WaitForSeconds(0.5f);
                }
                else
                {
                    // Try method 3: Force load more chunks and try again
                    DebugLog("No ground found with spiral search, force loading more chunks");
                    ForceLoadSurroundingChunks(transform.position, forceLoadDistance + 1);
                    yield return new WaitForSeconds(1.0f);
                    
                    // One final attempt to find ground
                    if (FindGroundPosition(out Vector3 finalPosition))
                    {
                        TeleportTo(finalPosition);
                        isGroundFound = true;
                        DebugLog($"Found ground after extended search: {finalPosition}");
                    }
                }
            }
        }
        
        // Final check before releasing controls
        if (isGroundFound)
        {
            DebugLog("Ground confirmed, enabling player controls");
            EnablePlayerControl();
        }
        else
        {
            // If we still can't find ground, try absolute last resort position
            DebugLog("WARNING: No ground confirmed, using last resort position");
            TryFallbackSpawnerPosition();
            
            // Give extended gravity suspension and enable controls
            SuspendGravity(20f);
            EnablePlayerControl();
        }
    }
    
    private IEnumerator WaitForTerrainAndGround()
    {
        float elapsedTime = 0f;
        
        while (elapsedTime < maxTerrainWaitTime && !isGroundFound)
        {
            // Check if we should allow hover movement
            if (allowHoverMovement && elapsedTime > 1f && IsOwner && playerController != null && !playerController.enabled)
            {
                // Allow movement while hovering after 1 second of waiting
                // But keep the gravity suspension active
                DebugLog("Enabling hover movement controls while waiting for ground");
                playerController.enabled = true;
                hasEnabledControls = true;
            }
            
            // Update elapsed time
            elapsedTime = Time.time - safetyStartTime;
            
            // Check for ground below current position
            if (FindGroundPosition(out Vector3 groundPosition))
            {
                // Found ground, teleport player there
                Vector3 safePos = groundPosition + Vector3.up * minHeightAboveGround;
                TeleportTo(safePos);
                isGroundFound = true;
                DebugLog($"Ground found at {groundPosition}, teleporting player to {safePos}");
                break;
            }
            
            // If not found, wait a bit before checking again
            yield return new WaitForSeconds(groundCheckInterval);
            
            // Every 2 seconds, force terrain reload
            if (Mathf.FloorToInt(elapsedTime) % 2 == 0)
            {
                ForcePriorityTerrainLoad();
            }
        }
        
        // If time expired without finding ground
        if (elapsedTime >= maxTerrainWaitTime && !isGroundFound)
        {
            DebugLog($"WARNING: Maximum wait time reached ({maxTerrainWaitTime}s) without finding ground");
        }
    }
    
    private bool FindGroundPosition(out Vector3 groundPosition)
    {
        groundPosition = Vector3.zero;
        
        // Cast rays from multiple positions
        Vector3[] testPositions = {
            transform.position, // Current position
            initialPosition, // Initial spawn position
            new Vector3(transform.position.x, transform.position.y - 10f, transform.position.z), // Lower current position
            new Vector3(transform.position.x, transform.position.y + 10f, transform.position.z), // Higher current position
            new Vector3(transform.position.x, transform.position.y + 50f, transform.position.z), // Much higher current position
            new Vector3(initialPosition.x, initialPosition.y + 50f, initialPosition.z) // Much higher initial position
        };
        
        foreach (Vector3 position in testPositions)
        {
            if (Physics.Raycast(position, Vector3.down, out RaycastHit hit, maxGroundCheckDistance, terrainLayer))
            {
                groundPosition = hit.point;
                return true;
            }
        }
        
        // If direct raycasts fail, try a spiral pattern search
        return FindGroundInSpiral(transform.position + Vector3.up * 50f, out groundPosition);
    }
    
    private bool FindGroundInSpiral(Vector3 center, out Vector3 groundPosition)
    {
        groundPosition = Vector3.zero;
        int rings = 5;
        float spacing = 10f;
        
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
                if (Physics.Raycast(testPoint, Vector3.down, out RaycastHit hit, maxGroundCheckDistance, terrainLayer))
                {
                    groundPosition = hit.point + Vector3.up * minHeightAboveGround;
                    return true;
                }
            }
        }
        
        return false;
    }
    
    private Vector3 FindSafePosition()
    {
        // Try to find a safe position in various ways
        
        // First try direct ground finding
        if (FindGroundPosition(out Vector3 groundPos))
        {
            return groundPos + Vector3.up * minHeightAboveGround;
        }
        
        // Then try using PlayerSpawner as fallback
        Vector3 fallbackPos = FindFallbackPosition();
        if (fallbackPos != Vector3.zero)
        {
            return fallbackPos;
        }
        
        // Try a high position with ground search
        Vector3 highPos = new Vector3(transform.position.x, transform.position.y + 100f, transform.position.z);
        if (FindGroundInSpiral(highPos, out Vector3 spiralGroundPos))
        {
            return spiralGroundPos;
        }
        
        // Last resort: Return a high position above current position
        return new Vector3(transform.position.x, transform.position.y + 100f, transform.position.z);
    }
    
    private bool IsGroundBelow(Vector3 position)
    {
        return Physics.Raycast(
            position + Vector3.up * 2f,
            Vector3.down,
            maxGroundCheckDistance,
            terrainLayer
        );
    }
    
    private Vector3 FindFallbackPosition()
    {
        // Try to use PlayerSpawner if available
        if (PlayerSpawner.Instance != null && networkObject != null)
        {
            Vector3 spawnPos = PlayerSpawner.Instance.GetSpawnPosition(networkObject.OwnerClientId);
            
            // Verify there's actually ground at this position
            if (IsGroundBelow(spawnPos))
            {
                return spawnPos;
            }
            
            // Try using FindGroundForPlayer as an alternative
            return PlayerSpawner.Instance.FindGroundForPlayer(networkObject.OwnerClientId, transform.position);
        }
        
        return Vector3.zero;
    }
    
    private void TryFallbackSpawnerPosition()
    {
        if (isFallbackPositionUsed) return; // Prevent loops
        
        isFallbackPositionUsed = true;
        
        if (PlayerSpawner.Instance != null && networkObject != null)
        {
            Vector3 lastResortPos = PlayerSpawner.Instance.GetSpawnPosition(networkObject.OwnerClientId);
            DebugLog($"Using absolute last resort position from PlayerSpawner: {lastResortPos}");
            TeleportTo(lastResortPos);
        }
        else
        {
            // No PlayerSpawner, go up high above current position
            Vector3 highPos = new Vector3(transform.position.x, 100f, transform.position.z);
            DebugLog($"No PlayerSpawner available, using high position: {highPos}");
            TeleportTo(highPos);
        }
    }
    
    private void ForcePriorityTerrainLoad()
    {
        if (World.Instance == null) return;
        
        // Update player position in World to trigger chunk loading
        World.Instance.UpdatePlayerPosition(transform.position);
        
        // Get the player's current chunk coordinates
        Vector3Int chunkCoord = Coord.WorldToChunkCoord(
            transform.position, 
            World.Instance.chunkSize, 
            World.Instance.voxelSize
        );
        
        // Prioritize loading chunks immediately around player
        if (ChunkOperationsQueue.Instance != null)
        {
            // Force load nearby chunks with priority
            for (int x = -forceLoadDistance; x <= forceLoadDistance; x++)
            for (int y = -forceLoadDistance; y <= forceLoadDistance; y++)
            for (int z = -forceLoadDistance; z <= forceLoadDistance; z++)
            {
                Vector3Int neighborCoord = chunkCoord + new Vector3Int(x, y, z);
                
                // Use immediate loading and disable quick check to ensure proper terrain loading
                ChunkOperationsQueue.Instance.QueueChunkForLoad(neighborCoord, true, !disableQuickCheck);
            }
            
            DebugLog($"Force loaded terrain chunks around {chunkCoord}");
        }
        
        // Request terrain analysis invalidation for nearby chunks
        for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
        for (int z = -1; z <= 1; z++)
        {
            Vector3Int coord = chunkCoord + new Vector3Int(x, y, z);
            TerrainAnalysisCache.InvalidateAnalysis(coord);
        }
    }

    private void ForceLoadSurroundingChunks(Vector3 position, int extraRadius = 0)
    {
        if (World.Instance == null) return;

        // Get the chunk coordinate from the position
        Vector3Int chunkCoord = Coord.WorldToChunkCoord(
            position,
            World.Instance.chunkSize,
            World.Instance.voxelSize
        );

        // Set the radius to use (default + extra)
        int radius = forceLoadDistance + extraRadius;

        // Queue chunk loads with CRITICAL priority and disable quick check
        if (ChunkOperationsQueue.Instance != null)
        {
            for (int x = -radius; x <= radius; x++)
            for (int y = -radius; y <= radius; y++)
            for (int z = -radius; z <= radius; z++)
            {
                Vector3Int neighborCoord = chunkCoord + new Vector3Int(x, y, z);
                
                // Use highest priority for center chunk and nearby chunks
                bool isImmediate = Mathf.Abs(x) <= 1 && Mathf.Abs(y) <= 1 && Mathf.Abs(z) <= 1;
                
                // CRITICAL: We always disable quick check for spawn chunks
                ChunkOperationsQueue.Instance.QueueChunkForLoad(neighborCoord, isImmediate, false);
            }

            DebugLog($"Force loaded {(radius*2+1)*(radius*2+1)*(radius*2+1)} chunks around {chunkCoord}");
        }
    }
    
    private void SuspendGravity(float duration = 10f)
    {
        // Record the component for proper tracking
        TemporaryGravitySuspension existingSuspension = gameObject.GetComponent<TemporaryGravitySuspension>();
        
        // Create or update gravity suspension component
        if (existingSuspension != null)
        {
            // Update existing component
            gravitySuspension = existingSuspension;
            gravitySuspension.SuspendGravity(duration);
            DebugLog($"Updated existing gravity suspension for {duration} seconds");
        }
        else
        {
            // Create new component
            gravitySuspension = gameObject.AddComponent<TemporaryGravitySuspension>();
            gravitySuspension.SuspendGravity(duration);
            DebugLog($"Created new gravity suspension for {duration} seconds");
        }
        
        // Also directly set rigidbody if present
        if (playerRigidbody != null)
        {
            playerRigidbody.useGravity = false;
            DebugLog("Disabled Rigidbody gravity");
        }
    }
    
    private void TeleportTo(Vector3 position)
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
        
        // Try to use NetworkPlayer.TeleportClientRpc if available
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
            catch (System.Exception e)
            {
                Debug.LogError($"Error invoking TeleportClientRpc: {e.Message}");
            }
        }
        
        DebugLog($"Teleported player to {position}");
    }
    
    private void EnablePlayerControl()
    {
        // Only do this for the local player
        if (!IsOwner) return;
        
        if (playerController != null)
        {
            // Only enable if currently disabled
            if (!playerController.enabled)
            {
                playerController.enabled = true;
                hasEnabledControls = true;
                
                float timeTaken = Time.time - controlDisableStartTime;
                DebugLog($"Player controls enabled after {timeTaken:F1} seconds");
            }
            else
            {
                DebugLog("Player controls already enabled");
            }
        }
        else
        {
            Debug.LogError("PlayerController is null when trying to enable controls");
        }
        
        // Mark as initialized
        isInitialized = true;
    }
    
    private void DebugLog(string message)
    {
        if (debugMode)
        {
            Debug.Log($"[PlayerSpawnSafety] {(networkObject != null ? $"Client {networkObject.OwnerClientId}: " : "")} {message}");
        }
    }
    
    // For external components to check if spawn safety has completed
    public bool IsInitializationComplete()
    {
        return isInitialized;
    }
}