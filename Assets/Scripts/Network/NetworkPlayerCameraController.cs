using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using Unity.Cinemachine;
using ControllerAssets;

public class NetworkPlayerCameraController : NetworkBehaviour
{
    [Header("Camera Settings")]
    [SerializeField] private GameObject cinemachineCameraPrefab;
    [SerializeField] private float cameraHeight = 1.6f;
    [SerializeField] private int cameraPriority = 100;
    [SerializeField] private string cameraTag = "PlayerCamera";

    [Header("Debug")]
    [SerializeField] private bool enableDebugLogs = true;

    // Runtime references
    private GameObject cameraTarget;
    private CinemachineCamera virtualCamera;
    private ThirdPersonController playerController;
    private Camera mainCamera;
    private CinemachineBrain cinemachineBrain;
    private bool setupComplete = false;
    private GameObject createdMainCameraObject;
    private bool subscribedToSceneEvents = false;

    public bool IsSetupComplete()
    {
        return setupComplete;
    }
    
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // Only perform camera setup for the local player
        if (!IsOwner) return;

        DebugLog($"Setting up camera for player {OwnerClientId}");
        
        // Get the player controller reference
        playerController = GetComponent<ThirdPersonController>();
        if (playerController == null)
        {
            DebugLog("ThirdPersonController component not found on player! Will retry on scene load.");
            return;
        }
        
        // Begin camera setup process
        StartCoroutine(SetupCameraSystem());

        SubscribeToSceneEvents();
    }

    private void SubscribeToSceneEvents()
    {
        if (subscribedToSceneEvents) return;

        SceneManager.sceneLoaded += HandleSceneLoaded;
        subscribedToSceneEvents = true;
    }

    private void UnsubscribeFromSceneEvents()
    {
        if (!subscribedToSceneEvents) return;

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        subscribedToSceneEvents = false;
    }

    private IEnumerator SetupCameraSystem()
    {
        // Wait a frame to allow scene to fully initialize
        yield return null;
        
        // Step 1: Find or create the main camera and ensure it has a CinemachineBrain
        SetupMainCamera();
        
        // Step 2: Create a camera target for the player
        CreateCameraTarget();
        
        // Step 3: Create or find the virtual camera
        yield return CreateVirtualCamera();
        
        // Step 4: Configure the ThirdPersonController with the right references
        ConfigurePlayerController();
        
        // Step 5: Validate the setup and log debug information
        ValidateSetup();
        
        // Mark setup as complete
        setupComplete = true;
        DebugLog("Camera setup complete");
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!IsOwner) return;

        // Re-run camera binding on the next frame to allow scene objects to initialise.
        StartCoroutine(ReacquireCameraAfterSceneLoad());
    }

    private IEnumerator ReacquireCameraAfterSceneLoad()
    {
        // Wait a frame so the scene can finish instantiating its contents.
        yield return null;

        SetupMainCamera();
        ConfigurePlayerController();

        if (virtualCamera != null && cameraTarget != null)
        {
            virtualCamera.Follow = cameraTarget.transform;
            virtualCamera.LookAt = cameraTarget.transform;
        }
    }

    private void SetupMainCamera()
    {
        // Find the main camera if it exists
        mainCamera = Camera.main;
        bool createdCamera = false;

        if (mainCamera == null)
        {
            DebugLog("Main camera not found, checking for any camera in scene");

            // Try to find any camera
            Camera[] cameras = FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.InstanceID);
            if (cameras.Length > 0)
            {
                mainCamera = cameras[0];
                DebugLog($"Using existing camera: {mainCamera.name}");
            }
            else
            {
                // Create a new main camera if none exists
                GameObject cameraObj = new GameObject("Main Camera");
                mainCamera = cameraObj.AddComponent<Camera>();
                mainCamera.tag = "MainCamera";
                DebugLog("Created new main camera");
                createdCamera = true;
                createdMainCameraObject = cameraObj;

                RemoveAudioListeners(cameraObj);
            }
        }
        else
        {
            DebugLog($"Found main camera: {mainCamera.name}");
        }

        if (createdCamera && mainCamera != null)
        {
            DontDestroyOnLoad(mainCamera.gameObject);
        }

        // Make sure the main camera has a CinemachineBrain
        cinemachineBrain = mainCamera.GetComponent<CinemachineBrain>();
        if (cinemachineBrain == null)
        {
            cinemachineBrain = mainCamera.gameObject.AddComponent<CinemachineBrain>();
            DebugLog("Added CinemachineBrain to main camera");
        }

        // Add a child tagged object to help finding the camera
        bool hasTaggedChild = false;
        foreach (Transform child in mainCamera.transform)
        {
            if (child.CompareTag(cameraTag))
            {
                hasTaggedChild = true;
                break;
            }
        }

        if (!hasTaggedChild)
        {
            try
            {
                GameObject taggedObj = new GameObject("CameraTaggedReference");
                taggedObj.transform.SetParent(mainCamera.transform, false);
                taggedObj.tag = cameraTag;
                DebugLog($"Added tagged child to main camera with tag '{cameraTag}'");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Could not tag camera reference: {ex.Message}");
            }
        }

        DisableExtraAudioListeners();
    }

    private void CreateCameraTarget()
    {
        // Create a target for the camera to follow at eye level
        cameraTarget = new GameObject($"CameraTarget_{OwnerClientId}");
        cameraTarget.transform.SetParent(transform);
        cameraTarget.transform.localPosition = new Vector3(0f, cameraHeight, 0f);
        
        // Immediately assign the target to the player controller
        if (playerController != null)
        {
            playerController.CinemachineCameraTarget = cameraTarget;
            DebugLog($"Created camera target at height {cameraHeight} and assigned to player controller");
        }
    }

    private IEnumerator CreateVirtualCamera()
    {
        // Check if there's an existing virtual camera for this player
        CinemachineCamera[] existingCameras = FindObjectsByType<CinemachineCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var cam in existingCameras)
        {
            if (cam.name.Contains($"PlayerCamera_{OwnerClientId}"))
            {
                virtualCamera = cam;
                DebugLog($"Found existing virtual camera: {virtualCamera.name}");
                
                // Configure the existing camera
                virtualCamera.Follow = cameraTarget.transform;
                virtualCamera.LookAt = cameraTarget.transform;
                virtualCamera.Priority.Value = cameraPriority;
                
                yield break;
            }
        }
        
        // Create a new virtual camera if none exists for this player
        GameObject cameraObject;
        
        // Use prefab if available
        if (cinemachineCameraPrefab != null)
        {
            cameraObject = Instantiate(cinemachineCameraPrefab);
            cameraObject.name = $"PlayerCamera_{OwnerClientId}";
            DebugLog($"Created camera from prefab: {cameraObject.name}");
        }
        else
        {
            // Create a basic camera if no prefab
            cameraObject = new GameObject($"PlayerCamera_{OwnerClientId}");
            DebugLog($"Created basic camera GameObject: {cameraObject.name}");
        }
        
        // Ensure the camera object persists between scenes
        DontDestroyOnLoad(cameraObject);
        
        // Get or add the virtual camera component
        virtualCamera = cameraObject.GetComponent<CinemachineCamera>();
        if (virtualCamera == null)
        {
            virtualCamera = cameraObject.GetComponentInChildren<CinemachineCamera>();
            
            // If still not found, add one
            if (virtualCamera == null)
            {
                virtualCamera = cameraObject.AddComponent<CinemachineCamera>();
                DebugLog("Added CinemachineCamera component to camera object");
            }
        }
        
        // Configure the virtual camera
        virtualCamera.Follow = cameraTarget.transform;
        virtualCamera.LookAt = cameraTarget.transform;
        virtualCamera.Priority.Value = cameraPriority;
        
        // Try to tag the camera object for easier finding
        try
        {
            cameraObject.tag = cameraTag;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Could not tag camera: {ex.Message}");
        }
        
        // Wait a frame for the camera to properly initialize
        yield return null;
    }

    private void ConfigurePlayerController()
    {
        if (playerController == null) return;
        
        // Ensure the controller has the target for rotations
        playerController.CinemachineCameraTarget = cameraTarget;
        
        // CRITICAL: The ThirdPersonController needs the main camera transform
        // for input direction calculation - this is key for mouse control to work properly
        if (mainCamera != null)
        {
            playerController.cameraTransform = mainCamera.transform;
            DebugLog($"Set player controller camera transform to {mainCamera.name}");
        }
        else
        {
            Debug.LogWarning("No main camera found for player controller reference!");
        }
    }

    private void ValidateSetup()
    {
        // Log the complete camera setup state for debugging
        DebugLog("------ Camera Setup Validation ------");
        
        if (playerController == null)
        {
            Debug.LogError("No ThirdPersonController reference!");
        }
        else
        {
            DebugLog($"ThirdPersonController: {playerController.name}");
            DebugLog($"-> CinemachineCameraTarget: {(playerController.CinemachineCameraTarget ? playerController.CinemachineCameraTarget.name : "NULL")}");
            DebugLog($"-> cameraTransform: {(playerController.cameraTransform ? playerController.cameraTransform.name : "NULL")}");
        }
        
        if (cameraTarget == null)
        {
            Debug.LogError("No camera target created!");
        }
        else
        {
            DebugLog($"Camera Target: {cameraTarget.name} at {cameraTarget.transform.localPosition}");
        }
        
        if (virtualCamera == null)
        {
            Debug.LogError("No virtual camera created!");
        }
        else
        {
            DebugLog($"Virtual Camera: {virtualCamera.name}");
            DebugLog($"-> Follow: {(virtualCamera.Follow ? virtualCamera.Follow.name : "NULL")}");
            DebugLog($"-> LookAt: {(virtualCamera.LookAt ? virtualCamera.LookAt.name : "NULL")}");
            DebugLog($"-> Priority: {virtualCamera.Priority.Value}");
        }
        
        if (mainCamera != null)
        {
            DebugLog($"Main Camera: {mainCamera.name}");
            DebugLog($"-> Has CinemachineBrain: {cinemachineBrain != null}");
        }
        
        DebugLog("------------------------------------");
    }

    private void LateUpdate()
    {
        if (!IsOwner || !setupComplete || playerController == null || cameraTarget == null) return;
        
        // This helps ensure the virtual camera stays active with correct priority
        if (virtualCamera != null && Time.frameCount % 60 == 0)  // Only refresh periodically
        {
            virtualCamera.Priority.Value = cameraPriority;
        }
    }

    public override void OnDestroy()
    {
        UnsubscribeFromSceneEvents();

        if (cameraTarget != null)
        {
            Destroy(cameraTarget);
        }

        if (createdMainCameraObject != null)
        {
            Destroy(createdMainCameraObject);
            createdMainCameraObject = null;
        }
        
        base.OnDestroy();
    }

    private void RemoveAudioListeners(GameObject target)
    {
        if (target == null) return;

        var listeners = target.GetComponentsInChildren<AudioListener>(true);
        if (listeners == null || listeners.Length == 0) return;

        foreach (var listener in listeners)
        {
            DebugLog($"Removing AudioListener from {listener.gameObject.name}");
            Destroy(listener);
        }
    }

    private void DisableExtraAudioListeners()
    {
        var listeners = FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (listeners == null || listeners.Length <= 1) return;

        foreach (var listener in listeners)
        {
            if (mainCamera != null && listener.gameObject == mainCamera.gameObject)
            {
                continue;
            }

            if (listener.enabled)
            {
                DebugLog($"Disabling extra AudioListener on {listener.gameObject.name}");
                listener.enabled = false;
            }
        }
    }

    private void DebugLog(string message)
    {
        if (enableDebugLogs)
        {
            Debug.Log($"[PlayerCamera-{OwnerClientId}] {message}");
        }
    }
}