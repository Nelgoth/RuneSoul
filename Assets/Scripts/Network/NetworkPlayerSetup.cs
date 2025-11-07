using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;
using ControllerAssets;
using System.Collections;

/// <summary>
/// Simplified Network Player Setup that focuses only on initializing player components
/// based on ownership. Camera setup is now handled by UnifiedPlayerCamera.
/// </summary>
public class NetworkPlayerSetup : NetworkBehaviour
{
    [Header("Component Setup")]
    [SerializeField] private bool enableDebugLogs = true;
    [SerializeField] private float initializationTimeout = 10f;
    [SerializeField] private float waitForCameraSetupTime = 2f;
    
    [Header("Player Safety")]
    [SerializeField] private bool enableSpawnSafety = true;
    [SerializeField] private float gravityEnableDelay = 2f;
    
    // References to player components that need ownership-based setup
    private PlayerInput playerInput;
    private ThirdPersonController playerController;
    private NetworkPlayerCameraController cameraController;
    private PlayerSpawnSafety spawnSafety;
    private PlayerFallRecovery fallRecovery;
    
    // Internal state tracking
    private bool isInitialized = false;
    private bool isCameraSetupComplete = false;
    private bool isControlEnabled = false;
    private Coroutine setupCoroutine;
    
    public bool IsFullyInitialized() => isInitialized;
    public bool IsCameraSetupComplete() => isCameraSetupComplete;
    public bool IsControlEnabled() => isControlEnabled;
    
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        DebugLog($"Network Player Spawned, IsOwner: {IsOwner}, ClientId: {OwnerClientId}");
        
        // Find all required components
        FindRequiredComponents();
        
        // Handle setup differently based on ownership
        if (IsOwner)
        {
            // Start asynchronous setup for local player
            setupCoroutine = StartCoroutine(SetupLocalPlayerAsync());
        }
        else
        {
            // Disable components for remote players immediately
            DisableNonOwnerComponents();
        }
    }
    
    public override void OnNetworkDespawn()
    {
        // Clean up any running coroutines
        if (setupCoroutine != null)
        {
            StopCoroutine(setupCoroutine);
            setupCoroutine = null;
        }
        
        base.OnNetworkDespawn();
    }

    private void FindRequiredComponents()
    {
        // Find controller components
        playerController = GetComponent<ThirdPersonController>();
        playerInput = GetComponent<PlayerInput>();
        
        // Find specialized network components
        cameraController = GetComponent<NetworkPlayerCameraController>();
        spawnSafety = GetComponent<PlayerSpawnSafety>();
        fallRecovery = GetComponent<PlayerFallRecovery>();
    }

    private IEnumerator SetupLocalPlayerAsync()
    {
        // Initially disable control to prevent falling through terrain
        DisableLocalPlayerControl();
        
        // First, wait for camera setup to complete if we have a camera controller
        if (cameraController != null)
        {
            float timeWaited = 0f;
            DebugLog("Waiting for camera setup to complete...");
            
            while (!cameraController.IsSetupComplete() && timeWaited < waitForCameraSetupTime)
            {
                yield return null;
                timeWaited += Time.deltaTime;
            }
            
            isCameraSetupComplete = cameraController.IsSetupComplete();
            DebugLog($"Camera setup completed: {isCameraSetupComplete}, waited {timeWaited:F2}s");
        }
        else
        {
            DebugLog("No camera controller found, skipping camera wait");
            isCameraSetupComplete = true;
        }
        
        // Next, if spawn safety is enabled, wait for it to complete
        if (enableSpawnSafety && spawnSafety != null)
        {
            DebugLog("Using spawn safety for player positioning");
            
            // Wait for spawn safety to complete
            float timeWaited = 0f;
            while (!spawnSafety.IsInitializationComplete() && timeWaited < initializationTimeout)
            {
                yield return null;
                timeWaited += Time.deltaTime;
            }
            
            if (spawnSafety.IsInitializationComplete())
            {
                DebugLog($"Spawn safety completed after {timeWaited:F2}s");
            }
            else
            {
                DebugLog($"Spawn safety timed out after {timeWaited:F2}s, continuing anyway");
            }
        }
        else
        {
            // If not using spawn safety, add a small delay to allow terrain to load
            DebugLog("No spawn safety component, adding safety delay before enabling control");
            yield return new WaitForSeconds(gravityEnableDelay);
        }
        
        // Now we can safely enable local player controls
        EnableLocalPlayerComponents();
        
        // Register with player spawner
        RegisterWithPlayerSpawner();
        
        // Track state
        isInitialized = true;
        DebugLog("Player initialization complete - full control enabled");
    }

    private void DisableLocalPlayerControl()
    {
        if (playerController != null)
        {
            playerController.enabled = false;
            DebugLog("Temporarily disabled ThirdPersonController for initial spawn safety");
        }
        
        if (playerInput != null)
        {
            playerInput.enabled = false;
            DebugLog("Temporarily disabled PlayerInput for initial spawn safety");
        }
    }

    private void EnableLocalPlayerComponents()
    {
        // Only enable if not already enabled
        if (isControlEnabled)
            return;
            
        // Enable controller components
        if (playerController != null)
        {
            playerController.enabled = true;
            DebugLog("Enabled ThirdPersonController for local player");
        }
        else
        {
            Debug.LogError("ThirdPersonController component not found on player!");
        }
        
        if (playerInput != null)
        {
            playerInput.enabled = true;
            DebugLog("Enabled PlayerInput for local player");
        }
        else
        {
            Debug.LogError("PlayerInput component not found on player!");
        }
        
        // Enable networking components that require owner control
        var networkAnimation = GetComponent<NetworkPlayerAnimation>();
        if (networkAnimation != null)
        {
            networkAnimation.enabled = true;
            DebugLog("Enabled NetworkPlayerAnimation for local player");
        }
        
        var playerInteraction = GetComponent<NetworkPlayerInteraction>();
        if (playerInteraction != null)
        {
            playerInteraction.enabled = true;
            DebugLog("Enabled NetworkPlayerInteraction for local player");
        }
        
        isControlEnabled = true;
    }
    
    private void DisableNonOwnerComponents()
    {
        // Disable input for non-owners
        if (TryGetComponent<PlayerInput>(out var input))
        {
            input.enabled = false;
            DebugLog("Disabled PlayerInput for non-owner");
        }
        
        // Disable controller for non-owners
        if (TryGetComponent<ThirdPersonController>(out var controller))
        {
            controller.enabled = false;
            DebugLog("Disabled ThirdPersonController for non-owner");
        }
        
        // Disable camera controller for non-owners
        if (TryGetComponent<NetworkPlayerCameraController>(out var camController))
        {
            camController.enabled = false;
            DebugLog("Disabled NetworkPlayerCameraController for non-owner");
        }
        
        // Disable other owner-only components
        if (TryGetComponent<PlayerSpawnSafety>(out var safety))
        {
            safety.enabled = false;
            DebugLog("Disabled PlayerSpawnSafety for non-owner");
        }
    }
    
    private void RegisterWithPlayerSpawner()
    {
        if (!IsOwner || !IsSpawned)
            return;
            
        // Register with the player spawner system if available
        if (PlayerSpawner.Instance != null)
        {
            PlayerSpawner.Instance.RegisterPlayerWithWorld(OwnerClientId, gameObject);
            DebugLog($"Registered player with PlayerSpawner system, ClientId: {OwnerClientId}");
        }
        else
        {
            Debug.LogWarning("PlayerSpawner.Instance not available - player position will not be saved");
        }
        
        // Update World system with player position (if available)
        if (World.Instance != null)
        {
            World.Instance.UpdatePlayerPosition(transform.position);
            DebugLog("Updated World system with player position");
        }
    }

    private void DebugLog(string message)
    {
        if (enableDebugLogs)
        {
            Debug.Log($"[PlayerSetup-{OwnerClientId}] {message}");
        }
    }
}