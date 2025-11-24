using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using System;

/// <summary>
/// Manages game state, scene transitions, and coordinates the overall game flow
/// </summary>
public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Scene Names")]
    [SerializeField] private string menuSceneName = "MenuScene";
    [SerializeField] private string loadingSceneName = "LoadingScene";
    [SerializeField] private string gameplaySceneName = "GameplayScene";

    [Header("Network Settings")]
    [SerializeField] private GameObject networkManagerPrefab;
    
    [Header("World Settings")]
    [SerializeField] private bool useLastWorldId = true;
    [SerializeField] private string defaultWorldId = "";

    // Game state tracking
    private bool isLoading = false;
    private ConnectionMode currentConnectionMode;
    
    // Connection error handling
    private string connectionErrorMessage = "";
    private bool hasConnectionError = false;
    
    // World selection settings
    private string selectedWorldId = "";
    private string selectedWorldName = "";
    private bool isMultiplayerWorld = true;

    // Enum to track how we're connected
    public enum ConnectionMode 
    {
        None,
        Host,
        Client,
        Server
    }

    private bool verboseDebugging = true; // Set to true to enable detailed logs

    private void Awake()
    {
        // Singleton pattern
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        
        Instance = this;
        DontDestroyOnLoad(gameObject);
        
        // Make sure we have a NetworkManager
        EnsureNetworkManagerExists();
    }

    private void VerboseLog(string message)
    {
        if (verboseDebugging)
        {
            Debug.Log($"[GameManager-VERBOSE] {message}");
        }
    }

    private void EnsureNetworkManagerExists()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.Log("GameManager: NetworkManager.Singleton is null, attempting to create from prefab");
            
            if (networkManagerPrefab != null)
            {
                GameObject networkManagerObj = Instantiate(networkManagerPrefab);
                Debug.Log($"GameManager: NetworkManager instantiated from prefab: {networkManagerObj.name}");
                
                // Verify it was created successfully
                if (NetworkManager.Singleton == null)
                {
                    Debug.LogError("GameManager: NetworkManager still null after instantiation!");
                }
            }
            else
            {
                Debug.LogError("GameManager: networkManagerPrefab is null! Cannot create NetworkManager instance.");
            }
        }
        else
        {
            Debug.Log($"GameManager: NetworkManager.Singleton already exists: {NetworkManager.Singleton.gameObject.name}");
        }
    }

    /// <summary>
    /// Public wrapper so that other systems can ensure the NetworkManager exists without duplicating logic.
    /// </summary>
    public void EnsureNetworkManagerReady()
    {
        EnsureNetworkManagerExists();
    }

    public void SetWorldDetails(string worldId, string worldName, bool isMultiplayer)
    {
        selectedWorldId = worldId;
        selectedWorldName = worldName;
        isMultiplayerWorld = isMultiplayer;
        
        Debug.Log($"GameManager: Set world details - ID: {worldId}, Name: {worldName}, Multiplayer: {isMultiplayer}");
    }

    public void StartHostMode()
    {
        Debug.Log("[GameManager] StartHostMode called");
        if (isLoading)
        {
            Debug.LogWarning("[GameManager] Already loading, ignoring StartHostMode call");
            return;
        }

        // IMPORTANT: Explicitly set to Host mode, not Server mode
        currentConnectionMode = ConnectionMode.Host;
        Debug.Log("[GameManager] Connection mode set to Host (NOT server-only mode)");
        
        // Debug current NetworkManager state
        if (NetworkManager.Singleton != null)
        {
            Debug.Log($"[GameManager] NetworkManager exists - IsListening: {NetworkManager.Singleton.IsListening}, IsServer: {NetworkManager.Singleton.IsServer}, IsHost: {NetworkManager.Singleton.IsHost}, IsClient: {NetworkManager.Singleton.IsClient}");
            
            // Check transport configuration
            var transport = NetworkManager.Singleton.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
            if (transport != null)
            {
                Debug.Log($"[GameManager] Transport info - Address: {transport.ConnectionData.Address}, Port: {transport.ConnectionData.Port}");
            }
        }
        else
        {
            Debug.LogError("[GameManager] NetworkManager.Singleton is null!");
        }
        
        // Use the last selected world or default if available
        EnsureWorldSelected();
        
        // Add detailed logging for the LoadGameScene call
        Debug.Log($"[GameManager] About to call LoadGameScene with mode: {currentConnectionMode}, WorldId: {selectedWorldId}");
        LoadGameScene();
    }

    public void StartClientMode()
    {
        if (isLoading) return;

        currentConnectionMode = ConnectionMode.Client;
        
        // Use the last selected world or default if available
        EnsureWorldSelected();
        
        LoadGameScene();
    }

    public void StartServerMode()
    {
        if (isLoading) return;

        currentConnectionMode = ConnectionMode.Server;
        
        // Use the last selected world or default if available
        EnsureWorldSelected();
        
        LoadGameScene();
    }
    
    private void EnsureWorldSelected()
    {
        VerboseLog("EnsureWorldSelected called");
        
        // If no world is explicitly selected, use the default or last one
        if (string.IsNullOrEmpty(selectedWorldId) && useLastWorldId)
        {
            VerboseLog("No world ID set, attempting to use default or last world");
            
            if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
            {
                // Use current world if one is already loaded
                selectedWorldId = WorldSaveManager.Instance.CurrentWorldId;
                VerboseLog($"Current WorldSaveManager world ID: {selectedWorldId}");
                
                if (string.IsNullOrEmpty(selectedWorldId) && !string.IsNullOrEmpty(defaultWorldId))
                {
                    // Try to load the default world
                    VerboseLog($"Attempting to load default world: {defaultWorldId}");
                    if (WorldSaveManager.Instance.LoadWorld(defaultWorldId))
                    {
                        selectedWorldId = defaultWorldId;
                        VerboseLog($"Loaded default world: {defaultWorldId}");
                    }
                    else
                    {
                        // Create a new default world
                        VerboseLog("Failed to load default world, creating new default world");
                        WorldSaveManager.Instance.InitializeWorld("Default World", true);
                        selectedWorldId = WorldSaveManager.Instance.CurrentWorldId;
                        VerboseLog($"Created new default world: {selectedWorldId}");
                    }
                }
            }
            else
            {
                VerboseLog("WorldSaveManager not available or not initialized");
            }
        }
        else
        {
            VerboseLog($"Using explicitly selected world ID: {selectedWorldId}");
        }
    }

    private bool PerformPreflightChecks()
    {
        Debug.Log("[GameManager] Performing preflight checks before scene transition");
        
        // Check for WorldSaveManager
        if (WorldSaveManager.Instance == null)
        {
            Debug.LogError("[GameManager] WorldSaveManager.Instance is null during preflight check!");
            connectionErrorMessage = "WorldSaveManager not available";
            return false;
        }
        
        // Ensure NetworkManager exists
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[GameManager] NetworkManager.Singleton is null during preflight check!");
            EnsureNetworkManagerExists();
            
            if (NetworkManager.Singleton == null)
            {
                connectionErrorMessage = "NetworkManager not available";
                return false;
            }
        }
        
        // Verify world is loaded or set
        if (string.IsNullOrEmpty(selectedWorldId))
        {
            Debug.LogError("[GameManager] No world selected during preflight check!");
            connectionErrorMessage = "No world selected";
            return false;
        }
        
        Debug.Log("[GameManager] Preflight checks passed successfully");
        return true;
    }

    private void LoadGameScene()
    {
        Debug.Log("[GameManager] LoadGameScene called");
        Debug.Log($"[GameManager] Current scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
        Debug.Log($"[GameManager] Loading scene name: '{loadingSceneName}'");
        Debug.Log($"[GameManager] Gameplay scene name: '{gameplaySceneName}'");
        
        if (isLoading)
        {
            Debug.LogWarning("[GameManager] Already loading, ignoring LoadGameScene call");
            return;
        }
        
        if (string.IsNullOrEmpty(loadingSceneName))
        {
            Debug.LogError("[GameManager] loadingSceneName is null or empty!");
            return;
        }
        
        // ADDED: Perform preflight checks
        if (!PerformPreflightChecks())
        {
            Debug.LogError("[GameManager] Preflight checks failed, aborting scene transition");
            ReportConnectionError(connectionErrorMessage);
            return;
        }
        
        isLoading = true;
        hasConnectionError = false;
        
        Debug.Log($"[GameManager] Starting to load game with mode: {currentConnectionMode}, worldId: {selectedWorldId}");
        
        // CRITICAL FIX: Make sure the NetworkManager is fully initialized before scene transition
        EnsureNetworkManagerExists();
        
        // Check if the scene exists
        try
        {
            Debug.Log($"[GameManager] Attempting to load scene: {loadingSceneName}");
            StartCoroutine(LoadSceneWithProgress());
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GameManager] Exception in LoadGameScene: {ex.Message}\n{ex.StackTrace}");
            ReportConnectionError($"Error loading scene: {ex.Message}");
            isLoading = false;
        }
    }

    private IEnumerator LoadSceneWithProgress()
    {
        VerboseLog("LoadSceneWithProgress coroutine started");
        
        // Check if the loading scene exists before trying to load it
        if (string.IsNullOrEmpty(loadingSceneName))
        {
            Debug.LogError("loadingSceneName is null or empty!");
            ReportConnectionError("Loading scene name is not configured");
            yield break;
        }
        
        // Load the loading scene asynchronously
        VerboseLog($"Starting async load of loading scene: {loadingSceneName}");
        AsyncOperation asyncLoad = null;
        
        try
        {
            asyncLoad = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(loadingSceneName);
            VerboseLog("LoadSceneAsync called successfully");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Exception starting scene load: {ex.Message}\n{ex.StackTrace}");
            ReportConnectionError($"Error starting scene load: {ex.Message}");
            isLoading = false;
            yield break;
        }
        
        if (asyncLoad == null)
        {
            Debug.LogError($"Failed to start loading scene '{loadingSceneName}' - asyncLoad is null");
            ReportConnectionError($"Failed to load scene: {loadingSceneName}");
            isLoading = false;
            yield break;
        }
        
        VerboseLog("Setting allowSceneActivation to true");
        asyncLoad.allowSceneActivation = true;
        
        // Wait until the scene is fully loaded
        while (!asyncLoad.isDone)
        {
            VerboseLog($"Loading scene progress: {asyncLoad.progress:P}");
            yield return null;
        }
        
        VerboseLog($"Loading scene '{loadingSceneName}' loaded successfully");
        
        // After the loading scene is fully loaded, start the gameplay loading process
        VerboseLog("Waiting 0.5 seconds before starting gameplay loading process");
        yield return new WaitForSeconds(0.5f); // Give the loading scene time to initialize
        
        VerboseLog("Starting SafeLoadGameplayScene coroutine");
        StartCoroutine(SafeLoadGameplayScene());
    }
    private IEnumerator LoadGameplayScene()
    {
        // Give the loading scene a moment to initialize
        yield return new WaitForSeconds(0.5f);
        
        // Get the loading controller for progress updates
        LoadingSceneController loadingController = FindFirstObjectByType<LoadingSceneController>();
        if (loadingController == null)
        {
            Debug.LogError("GameManager: LoadingSceneController not found in the loading scene!");
            ReportConnectionError("Loading scene controller not found");
            yield break;
        }
        
        Debug.Log("GameManager: Loading controller found, beginning world initialization");
        
        // Update loading progress (can be expanded with actual loading steps)
        UpdateLoadingProgress(0.1f, "Initializing world...");
        
        // Ensure world is loaded
        if (WorldSaveManager.Instance != null && !string.IsNullOrEmpty(selectedWorldId))
        {
            Debug.Log($"GameManager: Loading world with ID: {selectedWorldId}");
            if (!WorldSaveManager.Instance.LoadWorld(selectedWorldId))
            {
                Debug.LogError($"GameManager: Failed to load world: {selectedWorldId}");
                ReportConnectionError($"Failed to load world: {selectedWorldId}");
                yield break;
            }
            UpdateLoadingProgress(0.2f, "World initialized successfully");
            Debug.Log("GameManager: World initialized successfully");
        }
        else
        {
            if (WorldSaveManager.Instance == null)
            {
                Debug.LogWarning("GameManager: WorldSaveManager.Instance is null");
            }
            if (string.IsNullOrEmpty(selectedWorldId))
            {
                Debug.LogWarning("GameManager: selectedWorldId is null or empty");
            }
            UpdateLoadingProgress(0.2f, "Using default world settings");
            Debug.Log("GameManager: Using default world settings");
        }
        
        UpdateLoadingProgress(0.3f, "Initializing network...");
        Debug.Log("GameManager: Beginning network initialization");
        
        // Set up NetworkManager based on connection mode
        bool networkStarted = StartNetworkMode();
        
        // If network failed to start, report error and return to menu
        if (!networkStarted)
        {
            Debug.LogError("GameManager: Failed to initialize network connection");
            ReportConnectionError("Failed to initialize network connection");
            yield break;
        }
        
        Debug.Log("GameManager: Network started successfully");
        
        // Update progress
        UpdateLoadingProgress(0.4f, "Connecting to game...");
        
        // Wait for network connection to establish
        float connectionTimeout = 15f; // 15 seconds timeout
        float connectionTimer = 0f;
        
        Debug.Log("GameManager: Waiting for network connection to establish...");
        
        while (!IsNetworkConnected() && connectionTimer < connectionTimeout)
        {
            connectionTimer += Time.deltaTime;
            float progress = 0.4f + (connectionTimer / connectionTimeout * 0.3f);
            UpdateLoadingProgress(progress, "Establishing connection...");
            yield return null;
        }
        
        // Check if connection timed out
        if (!IsNetworkConnected())
        {
            Debug.LogError("GameManager: Connection timed out");
            ReportConnectionError("Connection timed out");
            yield break;
        }
        
        Debug.Log("GameManager: Network connection established successfully");
        
        // Network is connected, load gameplay scene
        UpdateLoadingProgress(0.7f, "Loading game world...");
        
        // If we're the server/host, load the gameplay scene
        if (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost)
        {
            Debug.Log($"GameManager: Loading gameplay scene: {gameplaySceneName}");
            
            // Using NetworkManager to load the scene on all clients
            NetworkManager.Singleton.SceneManager.LoadScene(gameplaySceneName, LoadSceneMode.Single);
            
            Debug.Log("GameManager: Scene load requested via NetworkManager");
        }
        else
        {
            Debug.Log("GameManager: Waiting for server to load the gameplay scene");
        }
        
        // Wait for the scene to be loaded
        float sceneLoadTimeout = 30f; // 30 seconds timeout
        float sceneLoadTimer = 0f;
        bool sceneLoaded = false;
        
        // CRITICAL FIX: Check if scene is loaded (not active) - NetworkManager loads scenes async
        while (!sceneLoaded && sceneLoadTimer < sceneLoadTimeout)
        {
            // Check if the gameplay scene exists in loaded scenes
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.name == gameplaySceneName && scene.isLoaded)
                {
                    sceneLoaded = true;
                    Debug.Log($"GameManager: Detected gameplay scene loaded at index {i}");
                    break;
                }
            }
            
            if (!sceneLoaded)
            {
                sceneLoadTimer += Time.deltaTime;
                float progress = 0.7f + (sceneLoadTimer / sceneLoadTimeout * 0.2f);
                UpdateLoadingProgress(progress, "Finalizing game world...");
                yield return null;
            }
        }
        
        if (!sceneLoaded)
        {
            Debug.LogError("GameManager: Gameplay scene failed to load within timeout period");
            ReportConnectionError("Gameplay scene failed to load");
            yield break;
        }
        
        // Scene is loaded, finalize
        UpdateLoadingProgress(1.0f, "Ready!");
        Debug.Log("GameManager: Gameplay scene loaded successfully");
        isLoading = false;
    }

    private IEnumerator SafeLoadGameplayScene()
    {
        // Give the loading scene a moment to initialize
        yield return new WaitForSeconds(0.5f);
        
        // Get the loading controller for progress updates
        LoadingSceneController loadingController = FindFirstObjectByType<LoadingSceneController>();
        if (loadingController == null)
        {
            Debug.LogError("GameManager: LoadingSceneController not found in the loading scene!");
            ReportConnectionError("Loading scene controller not found");
            yield break;
        }
        
        Debug.Log("GameManager: Loading controller found, beginning world initialization");
        
        // Update loading progress (can be expanded with actual loading steps)
        UpdateLoadingProgress(0.1f, "Initializing world...");
        
        // Ensure world is loaded
        bool worldInitialized = false;
        
        try
        {
            if (WorldSaveManager.Instance != null && !string.IsNullOrEmpty(selectedWorldId))
            {
                Debug.Log($"GameManager: Loading world with ID: {selectedWorldId}");
                worldInitialized = WorldSaveManager.Instance.LoadWorld(selectedWorldId);
                
                if (!worldInitialized)
                {
                    Debug.LogError($"GameManager: Failed to load world: {selectedWorldId}");
                    ReportConnectionError($"Failed to load world: {selectedWorldId}");
                    yield break;
                }
                UpdateLoadingProgress(0.2f, "World initialized successfully");
                Debug.Log("GameManager: World initialized successfully");
            }
            else
            {
                if (WorldSaveManager.Instance == null)
                {
                    Debug.LogWarning("GameManager: WorldSaveManager.Instance is null");
                }
                if (string.IsNullOrEmpty(selectedWorldId))
                {
                    Debug.LogWarning("GameManager: selectedWorldId is null or empty");
                }
                UpdateLoadingProgress(0.2f, "Using default world settings");
                Debug.Log("GameManager: Using default world settings");
                worldInitialized = true;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"GameManager: Exception during world initialization: {ex.Message}\n{ex.StackTrace}");
            ReportConnectionError($"World initialization error: {ex.Message}");
            yield break;
        }
        
        if (!worldInitialized)
        {
            ReportConnectionError("Failed to initialize world");
            yield break;
        }
        
        UpdateLoadingProgress(0.3f, "Initializing network...");
        Debug.Log("GameManager: Beginning network initialization");
        
        // Set up NetworkManager based on connection mode
        bool networkStarted = false;
        try
        {
            networkStarted = StartNetworkMode();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"GameManager: Exception starting network: {ex.Message}\n{ex.StackTrace}");
            ReportConnectionError($"Network start error: {ex.Message}");
            yield break;
        }
        
        // If network failed to start, report error and return to menu
        if (!networkStarted)
        {
            Debug.LogError("GameManager: Failed to initialize network connection");
            ReportConnectionError("Failed to initialize network connection");
            yield break;
        }
        
        Debug.Log("GameManager: Network started successfully");
        
        // Update progress
        UpdateLoadingProgress(0.4f, "Connecting to game...");
        
        // Wait for network connection to establish
        float connectionTimeout = 15f; // 15 seconds timeout
        float connectionTimer = 0f;
        
        Debug.Log("GameManager: Waiting for network connection to establish...");
        
        while (!IsNetworkConnected() && connectionTimer < connectionTimeout)
        {
            connectionTimer += Time.deltaTime;
            float progress = 0.4f + (connectionTimer / connectionTimeout * 0.3f);
            UpdateLoadingProgress(progress, "Establishing connection...");
            yield return null;
        }
        
        // Check if connection timed out
        if (!IsNetworkConnected())
        {
            Debug.LogError("GameManager: Connection timed out");
            ReportConnectionError("Connection timed out");
            yield break;
        }
        
        Debug.Log("GameManager: Network connection established successfully");
        
        // Network is connected, load gameplay scene
        UpdateLoadingProgress(0.7f, "Loading game world...");
        
        // If we're the server/host, load the gameplay scene
        if (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost)
        {
            Debug.Log($"GameManager: Loading gameplay scene: {gameplaySceneName}");
            
            try
            {
                // Using NetworkManager to load the scene on all clients
                NetworkManager.Singleton.SceneManager.LoadScene(gameplaySceneName, LoadSceneMode.Single);
                Debug.Log("GameManager: Scene load requested via NetworkManager");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"GameManager: Error loading gameplay scene: {ex.Message}\n{ex.StackTrace}");
                ReportConnectionError($"Error loading gameplay scene: {ex.Message}");
                yield break;
            }
        }
        else
        {
            Debug.Log("GameManager: Waiting for server to load the gameplay scene");
        }
        
        // Wait for the scene to be loaded
        float sceneLoadTimeout = 30f; // 30 seconds timeout
        float sceneLoadTimer = 0f;
        bool sceneLoaded = false;
        
        // CRITICAL FIX: Check if scene is loaded (not active) - NetworkManager loads scenes async
        while (!sceneLoaded && sceneLoadTimer < sceneLoadTimeout)
        {
            // Check if the gameplay scene exists in loaded scenes
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.name == gameplaySceneName && scene.isLoaded)
                {
                    sceneLoaded = true;
                    Debug.Log($"GameManager: Detected gameplay scene loaded at index {i}");
                    break;
                }
            }
            
            if (!sceneLoaded)
            {
                sceneLoadTimer += Time.deltaTime;
                float progress = 0.7f + (sceneLoadTimer / sceneLoadTimeout * 0.2f);
                UpdateLoadingProgress(progress, "Finalizing game world...");
                yield return null;
            }
        }
        
        if (!sceneLoaded)
        {
            Debug.LogError("GameManager: Gameplay scene failed to load within timeout period");
            ReportConnectionError("Gameplay scene failed to load");
            yield break;
        }
        
        // Scene is loaded, finalize
        UpdateLoadingProgress(1.0f, "Ready!");
        Debug.Log("GameManager: Gameplay scene loaded successfully");
        isLoading = false;
    }

    private void UpdateLoadingProgress(float progress, string status)
    {
        LoadingSceneController loadingController = FindFirstObjectByType<LoadingSceneController>();
        if (loadingController != null)
        {
            loadingController.UpdateProgress(progress, status);
        }
    }

    public bool IsServerOnlyMode()
    {
        return NetworkManager.Singleton != null && 
            NetworkManager.Singleton.IsServer && 
            !NetworkManager.Singleton.IsHost;
    }

    private bool StartNetworkMode()
    {
        VerboseLog($"StartNetworkMode called for mode: {currentConnectionMode}");
        
        try
        {
            // Check if NetworkManager is available
            if (NetworkManager.Singleton == null)
            {
                Debug.LogError("NetworkManager.Singleton is null! Cannot start network mode.");
                connectionErrorMessage = "NetworkManager not found";
                return false;
            }
            
            // Check if transport layer is available
            var transport = NetworkManager.Singleton.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
            if (transport == null)
            {
                Debug.LogError("UnityTransport component not found on NetworkManager!");
                connectionErrorMessage = "Network transport not configured";
                return false;
            }
            
            VerboseLog($"Transport info - Address: {transport.ConnectionData.Address}, Port: {transport.ConnectionData.Port}");
            
            // Check if we're using a relay
            bool usingRelay = transport.Protocol == Unity.Netcode.Transports.UTP.UnityTransport.ProtocolType.RelayUnityTransport;
            VerboseLog($"Using Relay: {usingRelay}");
            
            switch (currentConnectionMode)
            {
                case ConnectionMode.Host:
                    VerboseLog("Setting up NetworkManager for HOST mode");
                    
                    // Check if we're using relay through MultiplayerManager
                    if (MultiplayerManager.Instance != null && MultiplayerManager.Instance.IsInitialized)
                    {
                        VerboseLog("Using MultiplayerManager for host connection");
                        // The transport should already be configured by MultiplayerManager
                        string joinCode = MultiplayerManager.Instance.JoinCode;
                        VerboseLog($"Relay join code: {joinCode}");
                    }
                    else
                    {
                        VerboseLog("Using direct host mode without MultiplayerManager");
                        // Ensure transport is configured for direct connections if not using relay
                        if (!usingRelay)
                        {
                            transport.ConnectionData.Address = "0.0.0.0"; // Listen on all interfaces
                            transport.ConnectionData.Port = 7777; // Default port
                            VerboseLog("Set direct connection transport data: 0.0.0.0:7777");
                        }
                    }
                    
                    VerboseLog("Calling NetworkManager.StartHost()");
                    NetworkManager.Singleton.StartHost();
                    VerboseLog("NetworkManager.StartHost() completed");
                    break;
                        
                case ConnectionMode.Client:
                    // Similar debugging for Client mode
                    VerboseLog("Setting up NetworkManager for CLIENT mode");
                    NetworkManager.Singleton.StartClient();
                    break;
                        
                case ConnectionMode.Server:
                    // Similar debugging for Server mode
                    VerboseLog("Setting up NetworkManager for SERVER mode");
                    NetworkManager.Singleton.StartServer();
                    break;
                        
                default:
                    Debug.LogError("Invalid connection mode");
                    return false;
            }
            
            // Verify the final network state
            VerboseLog($"Network state after start - IsServer: {NetworkManager.Singleton.IsServer}, IsHost: {NetworkManager.Singleton.IsHost}, IsClient: {NetworkManager.Singleton.IsClient}");
            
            if (currentConnectionMode == ConnectionMode.Host && 
                NetworkManager.Singleton.IsServer && 
                !NetworkManager.Singleton.IsHost)
            {
                Debug.LogError("ERROR: Started in HOST mode but ended up in SERVER-ONLY mode!");
                VerboseLog("ERROR: HOST mode request resulted in SERVER-ONLY mode");
            }
            
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to start network mode: {e.Message}\n{e.StackTrace}");
            connectionErrorMessage = e.Message;
            return false;
        }
    }

    private bool IsNetworkConnected()
    {
        if (NetworkManager.Singleton == null)
        {
            VerboseLog("IsNetworkConnected: NetworkManager.Singleton is null");
            return false;
        }
        
        bool connected = false;
        
        switch (currentConnectionMode)
        {
            case ConnectionMode.Host:
                connected = NetworkManager.Singleton.IsHost;
                VerboseLog($"IsNetworkConnected (Host mode): {connected}");
                break;
                
            case ConnectionMode.Client:
                connected = NetworkManager.Singleton.IsClient;
                VerboseLog($"IsNetworkConnected (Client mode): {connected}");
                break;
                
            case ConnectionMode.Server:
                connected = NetworkManager.Singleton.IsServer;
                VerboseLog($"IsNetworkConnected (Server mode): {connected}");
                break;
                
            default:
                VerboseLog($"IsNetworkConnected (Unknown mode: {currentConnectionMode}): false");
                return false;
        }
        
        return connected;
    }

    private void ReportConnectionError(string error)
    {
        hasConnectionError = true;
        connectionErrorMessage = error;
        Debug.LogError($"Connection error: {error}");
        
        // Shutdown network if it was started
        if (NetworkManager.Singleton != null && 
            (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer))
        {
            NetworkManager.Singleton.Shutdown();
        }
        
        // Show error in the loading screen, or return to menu
        LoadingSceneController loadingController = FindFirstObjectByType<LoadingSceneController>();
        if (loadingController != null)
        {
            loadingController.ShowError(error);
        }
        else
        {
            // Return to menu if loading controller not found
            SceneManager.LoadScene(menuSceneName);
        }
        
        isLoading = false;
    }

    /// <summary>
    /// Gets the connection error for UI display
    /// </summary>
    public string GetConnectionError()
    {
        return connectionErrorMessage;
    }

    /// <summary>
    /// Check if we had a connection error
    /// </summary>
    public bool HasConnectionError()
    {
        return hasConnectionError;
    }

    /// <summary>
    /// Reset error state when returning to menu
    /// </summary>
    public void ResetErrorState()
    {
        hasConnectionError = false;
        connectionErrorMessage = "";
    }
    
    /// <summary>
    /// Get the current connection mode
    /// </summary>
    public ConnectionMode GetConnectionMode()
    {
        return currentConnectionMode;
    }
}