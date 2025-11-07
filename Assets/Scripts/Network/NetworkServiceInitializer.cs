using System.Collections;
using UnityEngine;

/// <summary>
/// Utility component that ensures proper initialization of network services
/// Attach this to a GameObject in your MenuScene
/// </summary>
public class NetworkServicesInitializer : MonoBehaviour
{
    [Header("Services")]
    [SerializeField] private bool initializeNetworkBridge = true;
    [SerializeField] private bool initializeMultiplayerManager = true;
    [SerializeField] private float initializationDelay = 0.5f;
    
    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    
    private void Start()
    {
        DebugLog("Starting network services initialization");
        StartCoroutine(InitializeServicesWithDelay());
    }
    
    private IEnumerator InitializeServicesWithDelay()
    {
        // Wait a moment to allow other systems to initialize
        yield return new WaitForSeconds(initializationDelay);
        
        // Initialize MultiplayerManager
        if (initializeMultiplayerManager && MultiplayerManager.Instance != null)
        {
            DebugLog("Initializing MultiplayerManager...");
            
            // Start initialization without try/catch in the coroutine
            var initTask = MultiplayerManager.Instance.InitializeServicesAsync();
            
            // Wait for initialization to complete
            while (!initTask.IsCompleted)
            {
                yield return null;
            }
            
            // Check result without try/catch
            if (initTask.IsCompletedSuccessfully && initTask.Result)
            {
                DebugLog("MultiplayerManager initialization successful");
                
                // Check the services status
                bool servicesAvailable = MultiplayerManager.Instance.CheckServicesStatus();
                DebugLog($"Multiplayer services available: {servicesAvailable}");
            }
            else
            {
                DebugLog("MultiplayerManager initialization failed");
            }
        }
        
        // Initialize NetworkConnectionBridge
        if (initializeNetworkBridge)
        {
            DebugLog("Initializing NetworkConnectionBridge...");
            NetworkConnectionBridge.Instance.Initialize();
            DebugLog("NetworkConnectionBridge initialized");
        }
        
        // Scan for UI components that need to subscribe to network events
        DebugLog("Scanning for UI components to attach to network services...");
        ConnectUIComponents();
        
        DebugLog("Network services initialization complete");
    }
    
    private void ConnectUIComponents()
    {
        // Find all relevant UI components and ensure they're connected
        var gameUIManager = FindObjectOfType<GameUIManager>();
        if (gameUIManager != null)
        {
            DebugLog("Found GameUIManager");
            
            // If GameUIManager has OnEnable method that needs to be called,
            // we can use this approach, but without try/catch in this context
            var enableMethod = gameUIManager.GetType().GetMethod("OnEnable", 
                System.Reflection.BindingFlags.NonPublic | 
                System.Reflection.BindingFlags.Instance);
                
            if (enableMethod != null)
            {
                enableMethod.Invoke(gameUIManager, null);
                DebugLog("Refreshed GameUIManager event subscriptions");
            }
        }
        else
        {
            DebugLog("No GameUIManager found in scene");
        }
    }
    
    private void DebugLog(string message)
    {
        if (verboseLogging)
        {
            Debug.Log($"[NetworkInitializer] {message}");
        }
    }
    
    // Add a static helper to create the initializer if needed
    public static void EnsureInitializer()
    {
        var existing = FindObjectOfType<NetworkServicesInitializer>();
        if (existing == null)
        {
            GameObject go = new GameObject("NetworkServicesInitializer");
            go.AddComponent<NetworkServicesInitializer>();
            Debug.Log("[NetworkInitializer] Created NetworkServicesInitializer dynamically");
        }
    }
}