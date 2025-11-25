using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Lobbies.Models;
using System.Linq;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;

/// <summary>
/// Central UI manager for the game's main menu and connection UI
/// Replaces the existing ConnectionUI script with a more streamlined approach
/// </summary>
public class GameUIManager : MonoBehaviour
{
    private static GameUIManager instance;
    public static GameUIManager Instance => instance;

    [Header("Main UI Containers")]
    [SerializeField] private GameObject mainMenuContainer;
    [SerializeField] private CanvasGroup mainMenuCanvasGroup;
    [SerializeField] private GameObject gameplayContainer;
    [SerializeField] private GameObject loadingContainer;
    [SerializeField] private GameObject errorContainer;

    [Header("Gameplay Menu")]
    [SerializeField] private Button saveAndDisconnectButton;
    [SerializeField] private Button resumeGameButton;

    [Header("Input Actions")]
    [SerializeField] private InputActionReference menuAction;
    
    [Header("Tab Navigation")]
    [SerializeField] private Button startGameTabButton;
    [SerializeField] private Button joinGameTabButton;
    [SerializeField] private GameObject startGamePanel;
    [SerializeField] private GameObject joinGamePanel;
    
    [Header("Start Game Panel")]
    [SerializeField] private Transform worldListContent;
    [SerializeField] private GameObject worldEntryPrefab;
    [SerializeField] private Button newWorldButton;
    [SerializeField] private Button deleteWorldButton;
    [SerializeField] private Button startGameButton;
    [SerializeField] private Toggle multiplayerToggle;
    [SerializeField] private TMP_InputField passwordInput;
    [SerializeField] private TMP_Text startGameStatusText;
    
    [Header("New World Panel")]
    [SerializeField] private GameObject newWorldPanel;
    [SerializeField] private TMP_InputField worldNameInput;
    [SerializeField] private TMP_InputField seedInput;
    [SerializeField] private Toggle newWorldMultiplayerToggle;
    [SerializeField] private Button createWorldButton;
    [SerializeField] private Button cancelNewWorldButton;
    
    [Header("Join Game Panel")]
    [SerializeField] private Button directConnectButton;
    [SerializeField] private Button joinByCodeButton;
    [SerializeField] private Button browseLobbyButton;
    [SerializeField] private TMP_Text joinGameStatusText;
    
    [Header("Direct Connect Panel")]
    [SerializeField] private GameObject directConnectPanel;
    [SerializeField] private TMP_InputField ipAddressInput;
    [SerializeField] private TMP_InputField portInput;
    [SerializeField] private Button connectDirectButton;
    [SerializeField] private Button backFromDirectButton;
    
    [Header("Join Code Panel")]
    [SerializeField] private GameObject joinCodePanel;
    [SerializeField] private TMP_InputField joinCodeInput;
    [SerializeField] private Button connectWithCodeButton;
    [SerializeField] private Button backFromCodeButton;
    
    [Header("Lobby Browser Panel")]
    [SerializeField] private GameObject lobbyBrowserPanel;
    [SerializeField] private Transform lobbyListContent;
    [SerializeField] private GameObject lobbyEntryPrefab;
    [SerializeField] private Button refreshLobbiesButton;
    [SerializeField] private Button backFromBrowserButton;
    [SerializeField] private TMP_Text lobbyBrowserStatusText;
    
    [Header("Loading Panel")]
    [SerializeField] private TMP_Text loadingStatusText;
    [SerializeField] private Slider loadingProgressBar;
    
    [Header("Error Panel")]
    [SerializeField] private TMP_Text errorMessageText;
    [SerializeField] private Button errorOkButton;
    
    // Internal state tracking
    private string selectedWorldId = "";
    private string selectedWorldName = "";
    private bool isMultiplayerWorld = true;
    private int selectedWorldSeed = 0;
    private bool isConnecting = false;
    private List<WorldMetadata> availableWorlds = new List<WorldMetadata>();
    private List<Lobby> availableLobbies = new List<Lobby>();
    private float lastLobbyRefreshTime = 0f;
    private const float MIN_LOBBY_REFRESH_INTERVAL = 5f;
    
    // Default network values
    private string defaultIpAddress = "127.0.0.1";
    private ushort defaultPort = 7777;
    
    private bool isInGameplayMode = false;
    private bool isGameplayMenuVisible = false;

    private void OnEnable()
    {
        if (instance != this) return;

        Debug.Log("GameUIManager OnEnable - setting up UI and initializing network connections");
        
        // Clear UI state when enabled - this helps prevent stacking panels
        
        // Only keep main menu container active
        if (mainMenuContainer) mainMenuContainer.SetActive(true);
        if (gameplayContainer) gameplayContainer.SetActive(false);
        if (loadingContainer) loadingContainer.SetActive(false);
        if (errorContainer) errorContainer.SetActive(false);
        
        // Hide all sub-panels
        if (startGamePanel) startGamePanel.SetActive(false);
        if (joinGamePanel) joinGamePanel.SetActive(false);
        if (newWorldPanel) newWorldPanel.SetActive(false);
        if (directConnectPanel) directConnectPanel.SetActive(false);
        if (joinCodePanel) joinCodePanel.SetActive(false);
        if (lobbyBrowserPanel) lobbyBrowserPanel.SetActive(false);
        
        // Show start game panel by default
        ShowStartGameTab();
        
        // CRITICAL: Initialize the NetworkConnectionBridge to ensure it's ready
        if (NetworkConnectionBridge.Instance != null)
        {
            Debug.Log("Ensuring NetworkConnectionBridge is initialized");
            NetworkConnectionBridge.Instance.Initialize();
            
            // Subscribe to NetworkConnectionBridge events
            NetworkConnectionBridge.Instance.OnConnectionResult -= OnBridgeConnectionResult;
            NetworkConnectionBridge.Instance.OnConnectionResult += OnBridgeConnectionResult;
        }
        else
        {
            Debug.LogWarning("NetworkConnectionBridge.Instance returned null!");
        }
        
        // Subscribe to MultiplayerManager events
        if (MultiplayerManager.Instance != null)
        {
            Debug.Log("Subscribing to MultiplayerManager events");
            // Unsubscribe first to prevent duplicate subscriptions
            MultiplayerManager.Instance.OnConnectionResult -= HandleConnectionResult;
            MultiplayerManager.Instance.OnLobbyCreated -= HandleLobbyCreated;
            MultiplayerManager.Instance.OnLobbyJoined -= HandleLobbyJoined;
            // Now subscribe
            MultiplayerManager.Instance.OnConnectionResult += HandleConnectionResult;
            MultiplayerManager.Instance.OnLobbyCreated += HandleLobbyCreated;
            MultiplayerManager.Instance.OnLobbyJoined += HandleLobbyJoined;
        }
        else
        {
            Debug.LogWarning("MultiplayerManager.Instance is null in OnEnable!");
            
            // Try to initialize multiplayer services
            Debug.Log("Attempting to ensure NetworkServicesInitializer is available");
            NetworkServicesInitializer.EnsureInitializer();
        }
        SceneManager.sceneLoaded += OnSceneLoaded;

        SetupMenuInputAction();
    }

    private void OnDisable()
    {
        if (instance != this) return;

        // Clean up event subscriptions
        SceneManager.sceneLoaded -= OnSceneLoaded;
        
        if (MultiplayerManager.Instance != null)
        {
            MultiplayerManager.Instance.OnConnectionResult -= HandleConnectionResult;
            MultiplayerManager.Instance.OnLobbyCreated -= HandleLobbyCreated;
            MultiplayerManager.Instance.OnLobbyJoined -= HandleLobbyJoined;
        }
        
        if (NetworkConnectionBridge.Instance != null)
        {
            NetworkConnectionBridge.Instance.OnConnectionResult -= OnBridgeConnectionResult;
        }

        TeardownMenuInputAction();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Debug.Log($"Scene loaded: {scene.name}, Mode: {mode}");
        
        // Check if we're entering gameplay scene
        if (scene.name == "GameplayScene")
        {
            isInGameplayMode = true;
            ShowGameplayScreen();
        }
        // Check if we're returning to menu scene
        else if (scene.name == "MenuScene")
        {
            isInGameplayMode = false;
            ShowMainMenu();
        }
        // Handle loading scene
        else if (scene.name == "LoadingScene")
        {
            // Keep current state, don't change anything
        }
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(transform.root.gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(transform.root.gameObject);

        EnsureGameplayContainerReference();

        // Make sure NetworkManager exists
        EnsureNetworkManagerExists();
        
        // Initialize network services
        InitializeNetworkServices();
    }
    
    private void Start()
    {
        SetupButtonListeners();
        ShowStartGameTab(); // Start with the Start Game tab
        SetDefaultValues();
    }
    
    private void Update()
    {
        // If we're in gameplay mode, ensure the UI stays in gameplay state
        if (isInGameplayMode && (mainMenuContainer && mainMenuContainer.activeSelf))
        {
            Debug.Log("Forcing UI back to gameplay state");
            ShowGameplayScreen();
        }
    }

    private void EnsureNetworkManagerExists()
    {
        if (NetworkManager.Singleton != null)
        {
            return;
        }

        // Attempt to use the GameManager to spin up the NetworkManager prefab
        GameManager gameManager = null;

        if (GameManager.Instance != null)
        {
            gameManager = GameManager.Instance;
        }
        else
        {
            gameManager = FindFirstObjectByType<GameManager>();

            // Instance might not yet be assigned if GameManager.Awake hasn't executed
            if (gameManager != null && GameManager.Instance == null)
            {
                Debug.Log("GameUIManager located GameManager before it finished initializing. Ensuring NetworkManager via direct call.");
            }
        }

        if (gameManager != null)
        {
            gameManager.EnsureNetworkManagerReady();
        }
        else
        {
            Debug.LogWarning("GameUIManager could not find a GameManager in the scene. Ensure a GameManager exists to create the NetworkManager prefab.");
        }

        if (NetworkManager.Singleton == null)
        {
            // Final fallback - check if a NetworkManager exists but singleton hasn't initialized yet
            var locatedNetworkManager = FindFirstObjectByType<NetworkManager>();

            if (locatedNetworkManager != null)
            {
                Debug.LogWarning("NetworkManager component found, waiting for singleton to initialize.");
                return;
            }

            Debug.LogError("NetworkManager not found! The UI won't function correctly.");
        }
    }
    
    private async void InitializeNetworkServices()
    {
        if (MultiplayerManager.Instance != null)
        {
            try
            {
                bool success = await MultiplayerManager.Instance.InitializeServicesAsync();
                if (success)
                {
                    Debug.Log("Multiplayer services initialized successfully");
                    
                    // Connect event handlers (unsubscribe first to prevent duplicates)
                    MultiplayerManager.Instance.OnConnectionResult -= HandleConnectionResult;
                    MultiplayerManager.Instance.OnLobbyCreated -= HandleLobbyCreated;
                    MultiplayerManager.Instance.OnLobbyJoined -= HandleLobbyJoined;
                    MultiplayerManager.Instance.OnConnectionResult += HandleConnectionResult;
                    MultiplayerManager.Instance.OnLobbyCreated += HandleLobbyCreated;
                    MultiplayerManager.Instance.OnLobbyJoined += HandleLobbyJoined;
                }
                else
                {
                    Debug.LogWarning("Failed to initialize multiplayer services");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error initializing multiplayer services: {ex.Message}");
            }
        }
        else
        {
            Debug.LogWarning("MultiplayerManager instance not found");
        }
    }
    
    private void SetupButtonListeners()
    {
        // Tab navigation
        if (startGameTabButton) startGameTabButton.onClick.AddListener(ShowStartGameTab);
        if (joinGameTabButton) joinGameTabButton.onClick.AddListener(ShowJoinGameTab);
        
        // Start Game panel
        if (newWorldButton) newWorldButton.onClick.AddListener(ShowNewWorldPanel);
        if (deleteWorldButton) deleteWorldButton.onClick.AddListener(DeleteSelectedWorld);
        if (startGameButton) startGameButton.onClick.AddListener(StartSelectedGame);
        
        // New World panel
        if (createWorldButton) createWorldButton.onClick.AddListener(CreateNewWorld);
        if (cancelNewWorldButton) cancelNewWorldButton.onClick.AddListener(HideNewWorldPanel);
        
        // Join Game panel
        if (directConnectButton) directConnectButton.onClick.AddListener(ShowDirectConnectPanel);
        if (joinByCodeButton) joinByCodeButton.onClick.AddListener(ShowJoinCodePanel);
        if (browseLobbyButton) browseLobbyButton.onClick.AddListener(ShowAndRefreshLobbyBrowser);
        
        // Direct Connect panel
        if (connectDirectButton) connectDirectButton.onClick.AddListener(ConnectDirectly);
        if (backFromDirectButton) backFromDirectButton.onClick.AddListener(ShowJoinGameTab);
        
        // Join Code panel
        if (connectWithCodeButton) connectWithCodeButton.onClick.AddListener(ConnectWithCode);
        if (backFromCodeButton) backFromCodeButton.onClick.AddListener(ShowJoinGameTab);
        
        // Lobby Browser panel
        if (refreshLobbiesButton) refreshLobbiesButton.onClick.AddListener(RefreshLobbyList);
        if (backFromBrowserButton) backFromBrowserButton.onClick.AddListener(ShowJoinGameTab);
        
        // Error panel
        if (errorOkButton) errorOkButton.onClick.AddListener(HideErrorPanel);
        
        // Gameplay menu
        if (saveAndDisconnectButton) saveAndDisconnectButton.onClick.AddListener(SaveAndDisconnect);
        if (resumeGameButton) resumeGameButton.onClick.AddListener(() => SetGameplayMenuVisible(false));
    }
    
    private void SetDefaultValues()
    {
        // Set default IP address and port
        if (ipAddressInput) ipAddressInput.text = defaultIpAddress;
        if (portInput) portInput.text = defaultPort.ToString();
        
        // Set default new world name and seed
        if (worldNameInput) worldNameInput.text = $"World_{DateTime.Now:yyyyMMdd_HHmmss}";
        if (seedInput) seedInput.text = UnityEngine.Random.Range(1, 99999).ToString();
        
        // Default to multiplayer enabled
        if (multiplayerToggle) multiplayerToggle.isOn = true;
        if (newWorldMultiplayerToggle) newWorldMultiplayerToggle.isOn = true;
    }
    
    #region UI Navigation
    private void EnsurePanelExclusivity(GameObject panelToShow)
    {
        Debug.Log($"Ensuring exclusivity for panel: {panelToShow.name}");
        
        // Organize panels into logical groups
        GameObject[] mainPanels = { startGamePanel, joinGamePanel };
        GameObject[] joinSubPanels = { directConnectPanel, joinCodePanel, lobbyBrowserPanel };
        GameObject[] startSubPanels = { newWorldPanel };
        
        // Handle main panels first (when showing start or join panel)
        if (panelToShow == startGamePanel || panelToShow == joinGamePanel)
        {
            foreach (var panel in mainPanels)
            {
                if (panel == null) continue;
                panel.SetActive(panel == panelToShow);
            }
            
            // Also hide all sub-panels
            foreach (var panel in joinSubPanels.Concat(startSubPanels))
            {
                if (panel == null) continue;
                panel.SetActive(false);
            }
        }
        // Handle join sub-panels
        else if (Array.IndexOf(joinSubPanels, panelToShow) >= 0)
        {
            // Hide all other join sub-panels but keep join main panel active
            foreach (var panel in joinSubPanels)
            {
                if (panel == null) continue;
                panel.SetActive(panel == panelToShow);
            }
            
            // Make sure join game panel is still visible
            if (joinGamePanel) joinGamePanel.SetActive(true);
        }
        // Handle start sub-panels
        else if (Array.IndexOf(startSubPanels, panelToShow) >= 0)
        {
            // Hide other start sub-panels but keep start main panel active
            foreach (var panel in startSubPanels)
            {
                if (panel == null) continue;
                panel.SetActive(panel == panelToShow);
            }
        }
    }
    
    private void HideNewWorldPanel()
    {
        // Hide new world panel
        if (newWorldPanel) newWorldPanel.SetActive(false);
        
        // Show start game panel
        if (startGamePanel) startGamePanel.SetActive(true);
    }

    private void ShowDirectConnectPanel()
    {
        Debug.Log("ShowDirectConnectPanel called - hiding other panels");
        
        // Explicitly hide the join game panel
        if (joinGamePanel) joinGamePanel.SetActive(false);
        
        // Hide other sub-panels
        if (joinCodePanel) joinCodePanel.SetActive(false);
        if (lobbyBrowserPanel) lobbyBrowserPanel.SetActive(false);
        
        // Show the direct connect panel
        if (directConnectPanel) directConnectPanel.SetActive(true);
    }
    
    private void ShowJoinCodePanel()
    {
        Debug.Log("ShowJoinCodePanel called - hiding other panels");
        
        // Explicitly hide the join game panel
        if (joinGamePanel) joinGamePanel.SetActive(false);
        
        // Hide other sub-panels
        if (directConnectPanel) directConnectPanel.SetActive(false);
        if (lobbyBrowserPanel) lobbyBrowserPanel.SetActive(false);
        
        // Show the join code panel
        if (joinCodePanel) joinCodePanel.SetActive(true);
    }
    
    private void ShowJoinGameTab()
    {
        // Ensure panel exclusivity
        if (joinGamePanel)
            EnsurePanelExclusivity(joinGamePanel);
        
        // Highlight the Join Game tab button
        if (startGameTabButton) startGameTabButton.interactable = true;
        if (joinGameTabButton) joinGameTabButton.interactable = false;
    }

    private void ShowStartGameTab()
    {
        // Ensure panel exclusivity
        if (startGamePanel)
            EnsurePanelExclusivity(startGamePanel);
        
        // Highlight the Start Game tab button
        if (startGameTabButton) startGameTabButton.interactable = false;
        if (joinGameTabButton) joinGameTabButton.interactable = true;
        
        // Refresh world list
        RefreshWorldList();
    }
    
    private void ShowNewWorldPanel()
    {
        // Ensure panel exclusivity
        if (newWorldPanel)
            EnsurePanelExclusivity(newWorldPanel);
        
        // Set default values
        if (worldNameInput) worldNameInput.text = $"World_{DateTime.Now:yyyyMMdd_HHmmss}";
        if (seedInput) seedInput.text = UnityEngine.Random.Range(1, 99999).ToString();
        if (newWorldMultiplayerToggle) newWorldMultiplayerToggle.isOn = true;
    }

    private void ShowAndRefreshLobbyBrowser()
    {
        Debug.Log("ShowAndRefreshLobbyBrowser called - hiding other panels");
        
        // Explicitly hide the join game panel
        if (joinGamePanel) joinGamePanel.SetActive(false);
        
        // Hide other sub-panels
        if (directConnectPanel) directConnectPanel.SetActive(false);
        if (joinCodePanel) joinCodePanel.SetActive(false);
        
        // Show the lobby browser panel
        if (lobbyBrowserPanel) lobbyBrowserPanel.SetActive(true);
        
        // Refresh lobby list
        RefreshLobbyList();
    }
    
    private void HideJoinSubPanels()
    {
        Debug.Log("Hiding all join sub-panels");
        
        // Hide all sub-panels explicitly
        if (directConnectPanel) directConnectPanel.SetActive(false);
        if (joinCodePanel) joinCodePanel.SetActive(false);
        if (lobbyBrowserPanel) lobbyBrowserPanel.SetActive(false);
    }
    
    private void ShowMainMenu()
    {
        // First hide all panels
        HideAllPanels();
        
        // Then show the main menu container
        if (mainMenuContainer) mainMenuContainer.SetActive(true);
        
        // Initially show the Start Game tab
        ShowStartGameTab();
        
        // Enable main menu interaction
        if (mainMenuCanvasGroup) mainMenuCanvasGroup.interactable = true;
        
        // Make cursor visible and unlocked
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        SetGameplayMenuVisible(false);
    }
    
    private void ShowLoadingScreen(string message, float progress = 0f)
    {
        // First hide all panels
        HideAllPanels();
        
        // Then show only the loading container
        if (loadingContainer) loadingContainer.SetActive(true);
        
        // Update loading text and progress
        if (loadingStatusText) loadingStatusText.text = message;
        if (loadingProgressBar) loadingProgressBar.value = progress;
    }
    
    public void SetGameplayLoadingOverlay(bool visible, float progress, string status)
    {
        if (loadingContainer)
        {
            loadingContainer.SetActive(visible);
        }

        if (!visible)
        {
            return;
        }

        if (loadingStatusText)
        {
            loadingStatusText.text = status;
        }

        if (loadingProgressBar)
        {
            loadingProgressBar.value = Mathf.Clamp01(progress);
        }
    }
    
    private void ShowErrorPanel(string errorMessage)
    {
        // Don't hide all panels here, just make sure the error panel is on top
        if (errorContainer) errorContainer.SetActive(true);
        if (errorMessageText) errorMessageText.text = errorMessage;
    }
    
    private void HideErrorPanel()
    {
        if (errorContainer) errorContainer.SetActive(false);
    }
    
    private void ShowGameplayScreen()
    {
        Debug.Log("Transitioning to gameplay screen");
        
        EnsureGameplayContainerReference();

        // First hide all panels explicitly including main containers
        if (mainMenuContainer) mainMenuContainer.SetActive(false);
        if (errorContainer) errorContainer.SetActive(false);
        if (loadingContainer) loadingContainer.SetActive(false);
        
        // Hide all panels of the Start Game tab
        if (startGamePanel) startGamePanel.SetActive(false);
        if (newWorldPanel) newWorldPanel.SetActive(false);
        
        // Hide all panels of the Join Game tab
        if (joinGamePanel) joinGamePanel.SetActive(false);
        if (directConnectPanel) directConnectPanel.SetActive(false);
        if (joinCodePanel) joinCodePanel.SetActive(false);
        if (lobbyBrowserPanel) lobbyBrowserPanel.SetActive(false);

        isInGameplayMode = true;

        // Ensure gameplay menu starts hidden and cursor locked for gameplay
        SetGameplayMenuVisible(false);
    }
    
    #endregion
    
    #region World Management

    private void EnsureGameplayContainerReference()
    {
        if (gameplayContainer != null)
        {
            gameplayContainer.SetActive(isGameplayMenuVisible);
            return;
        }

        // Try to find a child named GameplayContainer on this UI root
        var childTransform = transform.Find("GameplayContainer");
        if (childTransform != null)
        {
            gameplayContainer = childTransform.gameObject;
            gameplayContainer.SetActive(isGameplayMenuVisible);
            return;
        }

        // Fallback: search the scene by name
        var foundByName = GameObject.Find("GameplayContainer");
        if (foundByName != null)
        {
            gameplayContainer = foundByName;
            gameplayContainer.SetActive(isGameplayMenuVisible);
            return;
        }

        Debug.LogWarning("Gameplay container is null and could not be found automatically. Ensure it is assigned on the GameUIManager.");
    }
    
    private void RefreshWorldList()
    {
        // Update status
        UpdateStartGameStatus("Loading worlds...");
        
        if (WorldSaveManager.Instance == null)
        {
            UpdateStartGameStatus("Error: WorldSaveManager not available");
            return;
        }
        
        try
        {
            // Get available worlds
            availableWorlds.Clear();
            var worlds = WorldSaveManager.Instance.GetAvailableWorlds();
            availableWorlds.AddRange(worlds);
            
            // Clear existing world entries
            if (worldListContent != null)
            {
                foreach (Transform child in worldListContent)
                {
                    Destroy(child.gameObject);
                }
                
                // Create world entries
                if (worldEntryPrefab != null)
                {
                    foreach (var world in availableWorlds)
                    {
                        GameObject entryObj = Instantiate(worldEntryPrefab, worldListContent);
                        
                        // Try to use the SimplifiedWorldEntryUI component
                        WorldEntryUI entryUI = entryObj.GetComponent<WorldEntryUI>();
                        if (entryUI != null)
                        {
                            entryUI.Setup(world, SelectWorld);
                        }
                        // Fallback to WorldEntryUI if available
                        else
                        {
                            TMP_Text nameText = entryObj.GetComponentInChildren<TMP_Text>();
                            if (nameText != null)
                            {
                                string worldType = world.IsMultiplayerWorld ? "Multiplayer" : "Singleplayer";
                                nameText.text = $"{world.WorldName} [{worldType}]\nLast played: {world.LastPlayed.ToShortDateString()}";
                            }
                            
                            Button button = entryObj.GetComponentInChildren<Button>();
                            if (button != null)
                            {
                                string worldId = world.WorldId;
                                button.onClick.RemoveAllListeners();
                                button.onClick.AddListener(() => SelectWorld(worldId));
                            }
                        }
                    }
                }
            }
            
            UpdateStartGameStatus($"Found {availableWorlds.Count} worlds");
            
            // Disable delete and start buttons until a world is selected
            if (deleteWorldButton) deleteWorldButton.interactable = false;
            if (startGameButton) startGameButton.interactable = false;
        }
        catch (Exception ex)
        {
            UpdateStartGameStatus($"Error loading worlds: {ex.Message}");
            Debug.LogError($"Error refreshing world list: {ex.Message}\n{ex.StackTrace}");
        }
    }
    
    private void SelectWorld(string worldId)
    {
        // Clear previous selection
        foreach (Transform child in worldListContent)
        {
            WorldEntryUI entry = child.GetComponent<WorldEntryUI>();
            if (entry != null)
            {
                entry.SetSelected(false);
            }
        }
        
        // Find and highlight the selected entry
        foreach (Transform child in worldListContent)
        {
           WorldEntryUI entry = child.GetComponent<WorldEntryUI>();
            if (entry != null && entry.WorldId == worldId)
            {
                entry.SetSelected(true);
                break;
            }
        }
        // Find the world in the available worlds list
        WorldMetadata selectedWorld = null;
        foreach (var world in availableWorlds)
        {
            if (world.WorldId == worldId)
            {
                selectedWorld = world;
                break;
            }
        }
        
        if (selectedWorld == null)
        {
            UpdateStartGameStatus("Error: Selected world not found");
            return;
        }
        
        // Store the selection
        this.selectedWorldId = selectedWorld.WorldId;
        this.selectedWorldName = selectedWorld.WorldName;
        this.isMultiplayerWorld = selectedWorld.IsMultiplayerWorld;
        this.selectedWorldSeed = selectedWorld.WorldSeed;
        
        // Update UI to reflect selection
        if (multiplayerToggle) multiplayerToggle.isOn = isMultiplayerWorld;
        
        // Enable the delete and start buttons
        if (deleteWorldButton) deleteWorldButton.interactable = true;
        if (startGameButton) startGameButton.interactable = true;
        
        // Update the world entries to show selection
        UpdateWorldSelectionHighlight(worldId);
        
        UpdateStartGameStatus($"Selected world: {selectedWorldName}");
    }
    
    private void UpdateWorldSelectionHighlight(string selectedWorldId)
    {
        if (worldListContent == null) return;
        
        // Update all world entries
        foreach (Transform child in worldListContent)
        {
            // Try SimplifiedWorldEntryUI first
            WorldEntryUI entryUI = child.GetComponent<WorldEntryUI>();
            if (entryUI != null)
            {
                // Set selected state based on matching world ID
                entryUI.SetSelected(entryUI.WorldId == selectedWorldId);
            }
            else
            {
                // Fallback to basic approach if SimplifiedWorldEntryUI not found
                Debug.LogWarning("World entry without SimplifiedWorldEntryUI found. Consider updating all entries to use SimplifiedWorldEntryUI.");
                
                // Basic approach: update visual state of buttons 
                Button button = child.GetComponentInChildren<Button>();
                if (button != null)
                {
                    // Use interactable state to indicate selection (not ideal but functional)
                    button.interactable = (button.gameObject.name.Contains(selectedWorldId));
                }
            }
        }
    }
    
    private void CreateNewWorld()
    {
        if (string.IsNullOrEmpty(worldNameInput.text))
        {
            UpdateStartGameStatus("Please enter a world name");
            return;
        }
        
        // Get world details
        string worldName = worldNameInput.text;
        bool isMultiplayer = newWorldMultiplayerToggle != null && newWorldMultiplayerToggle.isOn;
        
        // Get seed
        int seed = UnityEngine.Random.Range(1, 99999);
        if (!string.IsNullOrEmpty(seedInput.text) && int.TryParse(seedInput.text, out int parsedSeed))
        {
            seed = parsedSeed;
        }
        
        // Create the world
        if (WorldSaveManager.Instance == null)
        {
            UpdateStartGameStatus("Error: WorldSaveManager not available");
            return;
        }
        
        try
        {
            // Initialize the world
            WorldSaveManager.Instance.InitializeWorld(worldName, isMultiplayer, seed);
            string worldId = WorldSaveManager.Instance.CurrentWorldId;
            
            // Store selection
            selectedWorldId = worldId;
            selectedWorldName = worldName;
            isMultiplayerWorld = isMultiplayer;
            selectedWorldSeed = seed;
            
            // Hide new world panel
            HideNewWorldPanel();
            
            // Refresh world list
            RefreshWorldList();
            
            // Log success
            UpdateStartGameStatus($"Created world: {worldName}");
        }
        catch (Exception ex)
        {
            UpdateStartGameStatus($"Error creating world: {ex.Message}");
            Debug.LogError($"Error creating world: {ex.Message}\n{ex.StackTrace}");
        }
    }
    
    private void DeleteSelectedWorld()
    {
        if (string.IsNullOrEmpty(selectedWorldId))
        {
            UpdateStartGameStatus("No world selected");
            return;
        }
        
        // TODO: Implement world deletion - Not critical for now
        UpdateStartGameStatus("World deletion not implemented yet");
    }
    
    private void OnBridgeConnectionResult(bool success, string message)
    {
        Debug.Log($"Bridge connection result: {success}, Message: {message}");
        
        if (!success)
        {
            // Handle error from the bridge
            HandleConnectionError(message);
        }
    }
    
    private void StartSelectedGame()
    {
        if (string.IsNullOrEmpty(selectedWorldId))
        {
            UpdateStartGameStatus("Please select a world first");
            return;
        }
        
        if (isConnecting)
        {
            UpdateStartGameStatus("Already connecting...");
            return;
        }
        
        // Update multiplayer setting based on toggle
        if (multiplayerToggle != null)
        {
            isMultiplayerWorld = multiplayerToggle.isOn;
        }
        
        // Start connection process
        isConnecting = true;
        UpdateStartGameStatus($"Starting world: {selectedWorldName}...");
        
        // CRITICAL FIX: Ensure WorldSaveManager knows about the selected world
        if (WorldSaveManager.Instance != null)
        {
            Debug.Log($"Ensuring WorldSaveManager has loaded the selected world: {selectedWorldId}");
            
            // First check if the world is already loaded
            if (WorldSaveManager.Instance.CurrentWorldId != selectedWorldId)
            {
                // Load the selected world
                bool success = WorldSaveManager.Instance.LoadWorld(selectedWorldId);
                if (!success)
                {
                    Debug.LogError($"Failed to load world {selectedWorldId} in WorldSaveManager");
                    HandleConnectionError("Failed to load world data");
                    return;
                }
                Debug.Log($"Successfully loaded world {selectedWorldId} in WorldSaveManager");
            }
            else
            {
                Debug.Log($"WorldSaveManager already has the correct world loaded: {selectedWorldId}");
            }
        }
        else
        {
            Debug.LogError("WorldSaveManager.Instance is null!");
            HandleConnectionError("WorldSaveManager not available");
            return;
        }
        
        // Use the NetworkConnectionBridge to handle network events
        if (NetworkConnectionBridge.Instance != null)
        {
            // Set world info in the bridge
            NetworkConnectionBridge.Instance.SetWorldInfo(selectedWorldId, selectedWorldName, isMultiplayerWorld);
            
            // Subscribe to bridge events if needed
            NetworkConnectionBridge.Instance.OnConnectionResult -= OnBridgeConnectionResult;
            NetworkConnectionBridge.Instance.OnConnectionResult += OnBridgeConnectionResult;
        }
        
        // Synchronize with GameManager
        if (GameManager.Instance != null)
        {
            try
            {
                // Set world details in GameManager
                GameManager.Instance.SetWorldDetails(selectedWorldId, selectedWorldName, isMultiplayerWorld);
                
                // Let GameManager handle loading scene transition
                if (isMultiplayerWorld && MultiplayerManager.Instance != null && MultiplayerManager.Instance.IsInitialized)
                {
                    // Start with lobby creation - use the bridge for better handling
                    NetworkConnectionBridge.Instance.StartHostWithLobby(selectedWorldName, false);
                }
                else
                {
                    // Start directly as host (without relay/lobby)
                    GameManager.Instance.StartHostMode();
                }
            }
            catch (Exception ex)
            {
                HandleConnectionError($"Error starting game: {ex.Message}");
            }
        }
        else
        {
            HandleConnectionError("GameManager not available");
        }
    }
    #endregion
    
    #region Join Game
    
    private void ConnectDirectly()
    {
        if (isConnecting)
        {
            UpdateJoinGameStatus("Already connecting...");
            return;
        }
        
        // Get IP and port
        string ipAddress = ipAddressInput != null ? ipAddressInput.text : defaultIpAddress;
        string portText = portInput != null ? portInput.text : defaultPort.ToString();
        
        if (string.IsNullOrEmpty(ipAddress))
        {
            UpdateJoinGameStatus("Please enter a valid IP address");
            return;
        }
        
        if (!ushort.TryParse(portText, out ushort port))
        {
            UpdateJoinGameStatus("Please enter a valid port number");
            return;
        }
        
        // Start connection process
        isConnecting = true;
        ShowLoadingScreen($"Connecting to {ipAddress}:{port}...");
        
        // Set up transport
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport != null)
        {
            transport.ConnectionData.Address = ipAddress;
            transport.ConnectionData.Port = port;
        }
        
        // Create a client world
        if (WorldSaveManager.Instance != null)
        {
            try
            {
                // Initialize a client world
                WorldSaveManager.Instance.InitializeClientWorld($"Client of {ipAddress}", true);
                string worldId = WorldSaveManager.Instance.CurrentWorldId;
                
                // Set world details in GameManager
                if (GameManager.Instance != null)
                {
                    GameManager.Instance.SetWorldDetails(worldId, $"Client of {ipAddress}", true);
                    
                    // Start client mode
                    GameManager.Instance.StartClientMode();
                }
                else
                {
                    HandleConnectionError("GameManager not available");
                }
            }
            catch (Exception ex)
            {
                HandleConnectionError($"Error initializing client world: {ex.Message}");
            }
        }
        else
        {
            HandleConnectionError("WorldSaveManager not available");
        }
    }
    
    private void ConnectWithCode()
    {
        if (isConnecting)
        {
            UpdateJoinGameStatus("Already connecting...");
            return;
        }
        
        // Get join code
        string joinCode = joinCodeInput != null ? joinCodeInput.text.Trim() : "";
        
        if (string.IsNullOrEmpty(joinCode))
        {
            UpdateJoinGameStatus("Please enter a valid join code");
            return;
        }
        
        // Clean the join code to ensure it's valid
        string cleanedCode = CleanLobbyCode(joinCode);
        
        // Start connection process
        isConnecting = true;
        ShowLoadingScreen($"Joining game with code: {cleanedCode}...");
        
        // Use the NetworkConnectionBridge to handle joining
        if (NetworkConnectionBridge.Instance != null)
        {
            // Subscribe to bridge events if needed
            NetworkConnectionBridge.Instance.OnConnectionResult -= OnBridgeConnectionResult;
            NetworkConnectionBridge.Instance.OnConnectionResult += OnBridgeConnectionResult;
            
            // Join using the bridge
            NetworkConnectionBridge.Instance.JoinLobbyByCode(cleanedCode);
        }
        else if (MultiplayerManager.Instance != null && MultiplayerManager.Instance.IsInitialized)
        {
            try
            {
                // Fallback to direct join
                _ = MultiplayerManager.Instance.JoinLobbyByCodeAsync(cleanedCode);
                // Success/failure handled by event callbacks
            }
            catch (Exception ex)
            {
                HandleConnectionError($"Error joining lobby: {ex.Message}");
            }
        }
        else
        {
            HandleConnectionError("Multiplayer services not available");
        }
    }
    
    private async void RefreshLobbyList()
    {
        // Check rate limiting
        float timeSinceLastRefresh = Time.time - lastLobbyRefreshTime;
        if (timeSinceLastRefresh < MIN_LOBBY_REFRESH_INTERVAL)
        {
            UpdateLobbyBrowserStatus($"Please wait {Mathf.CeilToInt(MIN_LOBBY_REFRESH_INTERVAL - timeSinceLastRefresh)}s before refreshing again");
            return;
        }
        
        lastLobbyRefreshTime = Time.time;
        UpdateLobbyBrowserStatus("Refreshing lobbies...");
        
        // Clear existing list before fetching
        if (lobbyListContent != null)
        {
            foreach (Transform child in lobbyListContent)
            {
                Destroy(child.gameObject);
            }
        }
        
        if (MultiplayerManager.Instance != null && MultiplayerManager.Instance.IsInitialized)
        {
            try
            {
                // Ensure fresh authentication before refreshing lobbies
                try
                {
                    if (!Unity.Services.Authentication.AuthenticationService.Instance.IsSignedIn)
                    {
                        await Unity.Services.Authentication.AuthenticationService.Instance.SignInAnonymouslyAsync();
                        Debug.Log("Re-authenticated before refreshing lobbies");
                    }
                }
                catch (Exception authEx)
                {
                    Debug.LogWarning($"Authentication refresh failed: {authEx.Message}, continuing anyway");
                }
                
                // Get lobbies
                availableLobbies = await MultiplayerManager.Instance.ListLobbiesAsync();
                int validLobbies = 0;
                
                // Create lobby entries
                if (lobbyListContent != null && lobbyEntryPrefab != null)
                {
                    foreach (var lobby in availableLobbies)
                    {
                        try
                        {
                            // First check if the lobby has our custom code
                            string codeToUse = null;
                            
                            // Prioritize CustomLobbyCode in lobby.Data
                            if (lobby.Data != null && lobby.Data.TryGetValue("CustomLobbyCode", out var customCodeData))
                            {
                                codeToUse = customCodeData.Value;
                                Debug.Log($"Found custom code in lobby data: {codeToUse}");
                            }
                            // Fall back to Unity's built-in lobby code if needed
                            else if (!string.IsNullOrEmpty(lobby.LobbyCode))
                            {
                                codeToUse = lobby.LobbyCode;
                                Debug.Log($"Using Unity lobby code: {codeToUse}");
                            }
                            
                            // Verify we have a valid code before creating the entry
                            if (string.IsNullOrEmpty(codeToUse))
                            {
                                Debug.LogWarning($"Skipping lobby {lobby.Name} - no valid code found");
                                continue;
                            }
                            
                            // Create entry with valid code - using uppercase for consistency
                            codeToUse = codeToUse.ToUpperInvariant();
                            GameObject entryObj = Instantiate(lobbyEntryPrefab, lobbyListContent);
                            validLobbies++;
                            
                            // Set up the UI element
                            LobbyListEntry entryUI = entryObj.GetComponent<LobbyListEntry>();
                            if (entryUI != null)
                            {
                                entryUI.SetupWithCustomCode(lobby, codeToUse, JoinLobbyWithCode);
                                Debug.Log($"Created lobby entry for {lobby.Name} with code {codeToUse}");
                            }
                            else
                            {
                                // Fallback to basic setup if LobbyListEntry component not found
                                Debug.LogWarning("No LobbyListEntry component found on lobby entry prefab");
                                
                                // Basic text and button setup
                                TMP_Text nameText = entryObj.GetComponentInChildren<TMP_Text>();
                                if (nameText != null)
                                {
                                    nameText.text = $"{lobby.Name} ({lobby.Players.Count}/{lobby.MaxPlayers})";
                                }
                                
                                Button button = entryObj.GetComponentInChildren<Button>();
                                if (button != null)
                                {
                                    string capturedCode = codeToUse;
                                    button.onClick.RemoveAllListeners();
                                    button.onClick.AddListener(() => JoinLobbyWithCode(capturedCode));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Error creating lobby entry: {ex.Message}");
                        }
                    }
                }
                
                UpdateLobbyBrowserStatus($"Found {validLobbies} compatible lobbies");
            }
            catch (Exception ex)
            {
                UpdateLobbyBrowserStatus($"Error refreshing lobbies: {ex.Message}");
                Debug.LogError($"Error refreshing lobbies: {ex.Message}\n{ex.StackTrace}");
            }
        }
        else
        {
            UpdateLobbyBrowserStatus("Multiplayer services not available");
        }
    }
    
    private bool IsValidRelayCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        
        // Only uppercase A-F are accepted by Unity Relay
        const string allowedChars = "ABCDEF";
        HashSet<char> validChars = new HashSet<char>(allowedChars);
        
        // Check each character
        foreach (char c in code)
        {
            if (!validChars.Contains(c))
            {
                Debug.LogWarning($"Invalid character in lobby code: '{c}' (U+{(int)c:X4})");
                return false;
            }
        }
        
        return true;
    }

    private string GetValidLobbyCode(Lobby lobby)
    {
        // First try the standard lobby code
        if (!string.IsNullOrWhiteSpace(lobby.LobbyCode))
        {
            return lobby.LobbyCode;
        }
        
        // Then check for custom code in data
        if (lobby.Data != null)
        {
            // Try CustomLobbyCode
            if (lobby.Data.TryGetValue("CustomLobbyCode", out var customCode) && 
                !string.IsNullOrWhiteSpace(customCode.Value))
            {
                return customCode.Value;
            }
            
            // Try RelayJoinCode as fallback
            if (lobby.Data.TryGetValue("RelayJoinCode", out var relayCode) && 
                !string.IsNullOrWhiteSpace(relayCode.Value))
            {
                return relayCode.Value;
            }
        }
        
        // No valid code found
        return string.Empty;
    }

    private void JoinLobbyWithCode(string lobbyCode)
    {
        if (isConnecting)
        {
            UpdateLobbyBrowserStatus("Already connecting...");
            return;
        }
        
        if (string.IsNullOrWhiteSpace(lobbyCode))
        {
            UpdateLobbyBrowserStatus("Invalid lobby code");
            return;
        }
        
        Debug.Log($"Attempting to join lobby with code: '{lobbyCode}'");
        
        // Start connection process
        isConnecting = true;
        ShowLoadingScreen($"Joining game with code: {lobbyCode}...");
        
        // Use the NetworkConnectionBridge to handle joining
        if (NetworkConnectionBridge.Instance != null)
        {
            // Subscribe to bridge events if needed
            NetworkConnectionBridge.Instance.OnConnectionResult -= OnBridgeConnectionResult;
            NetworkConnectionBridge.Instance.OnConnectionResult += OnBridgeConnectionResult;
            
            // Join using the bridge - but skip validation that causes problems with browser flow
            // The MultiplayerManager handles validation and joining differently based on context
            try {
                // Use the direct method in MultiplayerManager if needed
                if (MultiplayerManager.Instance != null && MultiplayerManager.Instance.IsInitialized)
                {
                    StartCoroutine(JoinFromBrowser(lobbyCode));
                }
                else
                {
                    HandleConnectionError("Multiplayer services not available");
                }
            }
            catch (Exception ex) {
                HandleConnectionError($"Error joining: {ex.Message}");
            }
        }
        else
        {
            HandleConnectionError("Multiplayer services not available");
        }
    }
    
    private IEnumerator JoinFromBrowser(string lobbyCode)
    {
        Debug.Log($"Starting join process from browser with code: {lobbyCode}");
        
        bool joinSuccess = false;
        string errorMessage = "";
        
        // Start the async task
        var joinTask = MultiplayerManager.Instance.JoinLobbyByCodeAsync(lobbyCode);
        
        // Wait for the task to complete
        while (!joinTask.IsCompleted)
        {
            yield return null;
        }
        
        // Check the result
        if (joinTask.IsFaulted)
        {
            joinSuccess = false;
            errorMessage = joinTask.Exception?.InnerException?.Message ?? "Unknown error joining lobby";
            Debug.LogError($"Error joining lobby: {errorMessage}");
        }
        else
        {
            joinSuccess = joinTask.Result;
            
            if (!joinSuccess)
            {
                errorMessage = "Failed to join lobby";
                Debug.LogError(errorMessage);
            }
            else
            {
                Debug.Log("Successfully joined lobby from browser");
            }
        }
        
        // Handle the result
        if (!joinSuccess)
        {
            HandleConnectionError(errorMessage);
        }
    }

    private IEnumerator JoinLobbyWithRetry(string lobbyCode)
    {
        int maxRetries = 3;
        float baseDelay = 1.0f;
        
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                Debug.Log($"Retry attempt {attempt+1}/{maxRetries} joining lobby with code: {lobbyCode}");
                yield return new WaitForSeconds(baseDelay * attempt);
            }
            
            // Start the task 
            Task<bool> joinTask = null;
            
            try
            {
                joinTask = MultiplayerManager.Instance.JoinLobbyByCodeAsync(lobbyCode);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception starting lobby join attempt {attempt+1}: {ex.GetType().Name}: {ex.Message}");
                
                if (attempt == maxRetries - 1)
                {
                    HandleConnectionError($"Error starting lobby join: {ex.Message}");
                    yield break;
                }
                
                continue; // Try again
            }
                
            // Wait for the task to complete with timeout
            float timeout = 10f;
            float elapsed = 0f;
            
            while (!joinTask.IsCompleted && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }
            
            // Handle various completion states
            if (!joinTask.IsCompleted)
            {
                Debug.LogError($"Timeout waiting for lobby join on attempt {attempt+1}");
                
                if (attempt == maxRetries - 1)
                {
                    HandleConnectionError("Lobby join timed out");
                    yield break;
                }
                
                continue; // Try again
            }
            
            if (joinTask.IsFaulted)
            {
                string errorMessage = joinTask.Exception != null ? 
                    joinTask.Exception.InnerException?.Message ?? joinTask.Exception.Message : 
                    "Unknown error";
                    
                Debug.LogError($"Error joining lobby on attempt {attempt+1}: {errorMessage}");
                
                if (attempt == maxRetries - 1)
                {
                    HandleConnectionError($"Failed to join lobby: {errorMessage}");
                    yield break;
                }
                
                continue; // Try again
            }
            
            if (joinTask.Result)
            {
                // Success!
                Debug.Log("Successfully joined lobby");
                yield break;
            }
            else
            {
                Debug.LogError($"Lobby join returned false on attempt {attempt+1}");
                
                if (attempt == maxRetries - 1)
                {
                    HandleConnectionError("Failed to join lobby - connection refused");
                    yield break;
                }
                
                continue; // Try again
            }
        }
        
        // If we get here, all retries failed
        HandleConnectionError("Failed to join lobby after multiple attempts");
    }

    private string CleanLobbyCode(string lobbyCode)
    {
        if (string.IsNullOrEmpty(lobbyCode))
        {
            return string.Empty;
        }
        
        // Remove problematic characters
        string cleaned = lobbyCode.Replace("'", "").Replace(" ", "").Trim();
        
        Debug.Log($"Cleaned lobby code: '{lobbyCode}' → '{cleaned}'");
        
        return cleaned;
    }

    #endregion
    
    #region Event Handlers
    
    private void HandleConnectionResult(bool success, string message)
    {
        if (!success)
        {
            HandleConnectionError(message);
        }
    }
    
    private void HideAllPanels()
    {
        // First hide the main containers
        if (mainMenuContainer) mainMenuContainer.SetActive(false);
        if (gameplayContainer) gameplayContainer.SetActive(false);
        if (loadingContainer) loadingContainer.SetActive(false);
        if (errorContainer) errorContainer.SetActive(false);

        // Then hide all sub-panels in the Start Game tab
        if (startGamePanel) startGamePanel.SetActive(false);
        if (newWorldPanel) newWorldPanel.SetActive(false);

        // Hide all sub-panels in the Join Game tab
        if (joinGamePanel) joinGamePanel.SetActive(false);
        if (directConnectPanel) directConnectPanel.SetActive(false);
        if (joinCodePanel) joinCodePanel.SetActive(false);
        if (lobbyBrowserPanel) lobbyBrowserPanel.SetActive(false);

        isGameplayMenuVisible = false;
    }

    private void HandleLobbyCreated(Lobby lobby)
    {
        Debug.Log($"[GameUIManager] Lobby created: {lobby.Name} (ID: {lobby.Id})");
        
        // Copy lobby code to clipboard for easy sharing
        if (!string.IsNullOrEmpty(lobby.LobbyCode))
        {
            GUIUtility.systemCopyBuffer = lobby.LobbyCode;
            Debug.Log($"[GameUIManager] Lobby code copied to clipboard: {lobby.LobbyCode}");
        }
        
        // NetworkConnectionBridge will trigger the actual host start.
        // We just keep the UI responsive here.
    }
    
    private void HandleLobbyJoined(Lobby lobby)
    {
        // Initialize client world
        if (WorldSaveManager.Instance != null)
        {
            try
            {
                // Create a client world
                WorldSaveManager.Instance.InitializeClientWorld(lobby.Name, true);
                string worldId = WorldSaveManager.Instance.CurrentWorldId;
                
                // Set world details in GameManager
                if (GameManager.Instance != null)
                {
                    GameManager.Instance.SetWorldDetails(worldId, lobby.Name, true);
                    
                    // Start client mode
                    GameManager.Instance.StartClientMode();
                }
                else
                {
                    HandleConnectionError("GameManager not available");
                }
            }
            catch (Exception ex)
            {
                HandleConnectionError($"Error initializing client world: {ex.Message}");
            }
        }
        else
        {
            HandleConnectionError("WorldSaveManager not available");
        }
    }
    
    private void HandleConnectionError(string errorMessage)
    {
        isConnecting = false;
        Debug.LogError($"Connection error: {errorMessage}");
        
        // Stop any ongoing connection
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkManager.Singleton.Shutdown();
        }
        
        // Show error message
        ShowErrorPanel(errorMessage);
    }
    
    #endregion
    
    #region Status Updates
    
    private void UpdateStartGameStatus(string message)
    {
        Debug.Log($"[Start Game] {message}");
        if (startGameStatusText != null)
        {
            startGameStatusText.text = message;
        }
    }
    
    private void UpdateJoinGameStatus(string message)
    {
        Debug.Log($"[Join Game] {message}");
        if (joinGameStatusText != null)
        {
            joinGameStatusText.text = message;
        }
    }
    
    private void UpdateLobbyBrowserStatus(string message)
    {
        Debug.Log($"[Lobby Browser] {message}");
        if (lobbyBrowserStatusText != null)
        {
            lobbyBrowserStatusText.text = message;
        }
    }
    
    #endregion
    
    private void OnDestroy()
    {
        if (instance == this)
        {
            instance = null;
        }

        SceneManager.sceneLoaded -= OnSceneLoaded;
        TeardownMenuInputAction();

        // Clean up event subscriptions
        if (MultiplayerManager.Instance != null)
        {
            MultiplayerManager.Instance.OnConnectionResult -= HandleConnectionResult;
            MultiplayerManager.Instance.OnLobbyCreated -= HandleLobbyCreated;
            MultiplayerManager.Instance.OnLobbyJoined -= HandleLobbyJoined;
        }
    }

    private void SetGameplayMenuVisible(bool visible)
    {
        isGameplayMenuVisible = visible;

        EnsureGameplayContainerReference();

        if (gameplayContainer != null)
        {
            gameplayContainer.SetActive(visible);
        }
        else if (visible)
        {
            Debug.LogWarning("Gameplay container is null! Unable to show gameplay menu.");
        }

        if (!isInGameplayMode)
        {
            return;
        }

        if (visible)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void ToggleGameplayMenu()
    {
        if (!isInGameplayMode)
        {
            return;
        }

        SetGameplayMenuVisible(!isGameplayMenuVisible);
    }
    
    private void SaveAndDisconnect()
    {
        Debug.Log("[GameUIManager] Save and Disconnect requested");
        
        // Save the world and all player positions
        if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
        {
            WorldSaveManager.Instance.SaveWorld();
            Debug.Log("[GameUIManager] World saved successfully");
        }
        
        // Disconnect from network
        if (NetworkManager.Singleton != null)
        {
            if (NetworkManager.Singleton.IsServer)
            {
                Debug.Log("[GameUIManager] Shutting down server");
                NetworkManager.Singleton.Shutdown();
            }
            else if (NetworkManager.Singleton.IsClient)
            {
                Debug.Log("[GameUIManager] Disconnecting client");
                NetworkManager.Singleton.Shutdown();
            }
        }
        
        // Return to main menu
        StartCoroutine(ReturnToMainMenuAfterDelay(0.5f));
    }
    
    private IEnumerator ReturnToMainMenuAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        
        // Load the menu scene
        Debug.Log("[GameUIManager] Returning to main menu");
        UnityEngine.SceneManagement.SceneManager.LoadScene("MenuScene");
    }

    private void SetupMenuInputAction()
    {
        if (menuAction == null)
        {
            Debug.LogWarning("Menu InputActionReference is not assigned on GameUIManager.");
            return;
        }

        var action = menuAction.action;
        if (action == null)
        {
            Debug.LogWarning("Menu InputActionReference is missing its action instance.");
            return;
        }

        action.performed -= OnMenuActionPerformed;
        action.performed += OnMenuActionPerformed;

        if (!action.enabled)
        {
            action.Enable();
        }
    }

    private void TeardownMenuInputAction()
    {
        if (menuAction == null) return;

        var action = menuAction.action;
        if (action == null) return;

        action.performed -= OnMenuActionPerformed;
    }

    private void OnMenuActionPerformed(InputAction.CallbackContext context)
    {
        if (!context.performed)
        {
            return;
        }

        ToggleGameplayMenu();
    }
}