using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using Unity.Services.Lobbies.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Bridge component that connects MultiplayerManager with the game's UI and world loading systems.
/// This ensures proper event handling and transitions between lobby creation and gameplay.
/// </summary>
public class NetworkConnectionBridge : MonoBehaviour
{
    private static NetworkConnectionBridge _instance;
    public static NetworkConnectionBridge Instance
    {
        get
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("NetworkConnectionBridge");
                _instance = go.AddComponent<NetworkConnectionBridge>();
                DontDestroyOnLoad(go);
                _instance.Initialize();
            }
            return _instance;
        }
    }

    [Header("Debug Settings")]
    [SerializeField] private bool enableDebugLogs = true;

    // State tracking
    private bool _isInitialized = false;
    private bool _isProcessingLobbyEvent = false;
    private string _selectedWorldId = "";
    private string _selectedWorldName = "";
    private bool _isMultiplayerWorld = true;
    
    // Connection result event
    public delegate void ConnectionEventHandler(bool success, string message);
    public event ConnectionEventHandler OnConnectionResult;

    public void Initialize()
    {
        if (_isInitialized) return;

        // Subscribe to MultiplayerManager events
        if (MultiplayerManager.Instance != null)
        {
            DebugLog("Subscribing to MultiplayerManager events");
            MultiplayerManager.Instance.OnLobbyCreated += HandleLobbyCreated;
            MultiplayerManager.Instance.OnLobbyJoined += HandleLobbyJoined;
            MultiplayerManager.Instance.OnConnectionResult += HandleConnectionResult;
        }
        else
        {
            Debug.LogError("MultiplayerManager instance not available!");
        }

        _isInitialized = true;
    }

    private void OnDestroy()
    {
        // Unsubscribe from events to prevent memory leaks
        if (MultiplayerManager.Instance != null)
        {
            MultiplayerManager.Instance.OnLobbyCreated -= HandleLobbyCreated;
            MultiplayerManager.Instance.OnLobbyJoined -= HandleLobbyJoined; 
            MultiplayerManager.Instance.OnConnectionResult -= HandleConnectionResult;
        }
    }

    /// <summary>
    /// Sets the world information to be used when starting a host.
    /// </summary>
    public void SetWorldInfo(string worldId, string worldName, bool isMultiplayer)
    {
        DebugLog($"Setting world info - ID: {worldId}, Name: {worldName}, Multiplayer: {isMultiplayer}");
        _selectedWorldId = worldId;
        _selectedWorldName = worldName;
        _isMultiplayerWorld = isMultiplayer;
    }

    /// <summary>
    /// Start the process of creating a lobby and starting as host.
    /// </summary>
    public void StartHostWithLobby(string lobbyName, bool isPrivate)
    {
        if (string.IsNullOrEmpty(_selectedWorldId))
        {
            Debug.LogError("Cannot start host: No world selected!");
            return;
        }

        DebugLog($"Starting host with lobby - Name: {lobbyName}, Private: {isPrivate}");

        // Make sure GameManager knows about the world
        if (GameManager.Instance != null)
        {
            GameManager.Instance.SetWorldDetails(_selectedWorldId, _selectedWorldName, _isMultiplayerWorld);
            DebugLog("World details synchronized with GameManager");
        }

        // Create the lobby through MultiplayerManager
        if (MultiplayerManager.Instance != null && MultiplayerManager.Instance.IsInitialized)
        {
            DebugLog("Creating lobby through MultiplayerManager");
            StartCoroutine(CreateLobbyWithTimeout(lobbyName, isPrivate));
        }
        else
        {
            Debug.LogError("MultiplayerManager not available or not initialized!");
            // Fallback to direct host start
            StartHostDirect();
        }
    }
    
    public void JoinLobbyByCode(string joinCode)
    {
        if (string.IsNullOrEmpty(joinCode))
        {
            Debug.LogError("Cannot join: Join code is empty!");
            OnConnectionResult?.Invoke(false, "Join code cannot be empty");
            return;
        }
        
        // Clean the join code by removing any problematic characters
        string cleanedJoinCode = CleanJoinCode(joinCode);
        
        if (string.IsNullOrEmpty(cleanedJoinCode))
        {
            Debug.LogError("Cannot join: Join code is invalid after cleaning");
            OnConnectionResult?.Invoke(false, "Invalid join code format");
            return;
        }
        
        DebugLog($"Joining lobby with code: {cleanedJoinCode}");
        
        // Join through MultiplayerManager
        if (MultiplayerManager.Instance != null && MultiplayerManager.Instance.IsInitialized)
        {
            DebugLog("Joining lobby through MultiplayerManager");
            StartCoroutine(JoinLobbyWithTimeout(cleanedJoinCode));
        }
        else
        {
            Debug.LogError("MultiplayerManager not available or not initialized!");
            OnConnectionResult?.Invoke(false, "Multiplayer services not available");
        }
    }

    private bool IsValidRelayCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        
        // Unity Relay codes can contain uppercase A-F and digits 0-9
        const string allowedChars = "ABCDEF0123456789";
        HashSet<char> validChars = new HashSet<char>(allowedChars);
        
        // Check each character
        foreach (char c in code)
        {
            if (!validChars.Contains(c))
            {
                DebugLog($"Invalid character in lobby code: '{c}' (U+{(int)c:X4})");
                return false;
            }
        }
        
        return true;
    }

    private string CleanJoinCode(string joinCode)
    {
        if (string.IsNullOrEmpty(joinCode))
        {
            return string.Empty;
        }

        // Remove problematic characters
        string cleaned = joinCode.Replace("'", "").Replace(" ", "").Trim();
        
        // Log the cleaning process
        DebugLog($"Cleaning join code: '{joinCode}' → '{cleaned}'");
        
        return cleaned;
    }

    public void StartHostDirect()
    {
        DebugLog("Starting host directly (bypassing multiplayer services)");

        if (string.IsNullOrEmpty(_selectedWorldId))
        {
            Debug.LogError("Cannot start host: No world selected!");
            return;
        }

        if (GameManager.Instance != null)
        {
            // Synchronize with GameManager
            GameManager.Instance.SetWorldDetails(_selectedWorldId, _selectedWorldName, _isMultiplayerWorld);
            
            // Use GameManager for scene transition
            try
            {
                GameManager.Instance.StartHostMode();
                DebugLog("Host mode started through GameManager");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error starting host mode: {ex.Message}\n{ex.StackTrace}");
            }
        }
        else
        {
            Debug.LogError("GameManager not available!");
        }
    }

    private IEnumerator CreateLobbyWithTimeout(string lobbyName, bool isPrivate)
    {
        DebugLog($"Creating lobby with timeout: {lobbyName}");
        
        bool lobbyCreated = false;
        Exception creationError = null;
        
        // Start async operation to create lobby
        var createTask = MultiplayerManager.Instance.CreateLobbyWithRelayAsync(lobbyName, isPrivate);
        
        // Set timeout of 15 seconds
        float timeout = 15f;
        float elapsed = 0f;
        
        // Wait for completion or timeout
        while (!createTask.IsCompleted && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }
        
        // Check result
        if (createTask.IsCompleted)
        {
            if (createTask.IsFaulted)
            {
                creationError = createTask.Exception;
                Debug.LogError($"Lobby creation failed: {creationError.Message}");
            }
            else
            {
                lobbyCreated = createTask.Result;
                DebugLog($"Lobby creation result: {lobbyCreated}");
            }
        }
        else
        {
            Debug.LogError("Lobby creation timed out!");
        }
        
        // If lobby creation failed or timed out, fall back to direct host start
        if (!lobbyCreated)
        {
            DebugLog("Falling back to direct host start");
            StartHostDirect();
        }
    }
    
    private IEnumerator JoinLobbyWithTimeout(string joinCode)
    {
        DebugLog($"Joining lobby with timeout: {joinCode}");
        
        bool lobbyJoined = false;
        
        // Retry logic
        int maxRetries = 3;
        float baseDelay = 1.0f;
        
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                DebugLog($"Retry attempt {attempt+1}/{maxRetries} joining lobby: {joinCode}");
                yield return new WaitForSeconds(baseDelay * attempt);
            }
            
            // Ensure authentication is fresh before each attempt
            if (!Unity.Services.Authentication.AuthenticationService.Instance.IsSignedIn)
            {
                DebugLog("Re-authenticating before joining lobby");
                
                // Start auth task
                var authTask = Unity.Services.Authentication.AuthenticationService.Instance.SignInAnonymouslyAsync();
                
                // Wait for auth with timeout
                float authTimeout = 5f;
                float authElapsed = 0f;
                
                while (!authTask.IsCompleted && authElapsed < authTimeout)
                {
                    authElapsed += Time.deltaTime;
                    yield return null;
                }
                
                if (authTask.IsCompleted && !authTask.IsFaulted)
                {
                    DebugLog("Re-authentication successful");
                }
                else
                {
                    Debug.LogWarning($"Re-authentication timed out or failed, continuing anyway");
                }
                
                // Small delay after auth
                yield return new WaitForSeconds(0.5f);
            }
            
            // Capture any exceptions from join attempt
            Exception joinError = null;
            
            // Start join task
            Task<bool> joinTask = null;
            
            try
            {
                joinTask = MultiplayerManager.Instance.JoinLobbyByCodeAsync(joinCode);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception starting lobby join: {ex.Message}");
                
                if (attempt == maxRetries - 1)
                {
                    OnConnectionResult?.Invoke(false, $"Error starting lobby join: {ex.Message}");
                    yield break;
                }
                
                continue; // Try again
            }
        
            // Set timeout of 15 seconds
            float timeout = 15f;
            float elapsed = 0f;
            
            // Wait for completion or timeout
            while (!joinTask.IsCompleted && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }
            
            // Log result of this attempt
            DebugLog($"Lobby join result: {joinTask.IsCompleted && !joinTask.IsFaulted && joinTask.Result}");
            
            // Check result
            if (!joinTask.IsCompleted)
            {
                DebugLog($"Lobby join attempt {attempt+1} timed out!");
                
                if (attempt == maxRetries - 1)
                {
                    OnConnectionResult?.Invoke(false, "Lobby join timed out");
                    yield break;
                }
                
                continue; // Try again
            }
            
            if (joinTask.IsFaulted)
            {
                joinError = joinTask.Exception;
                string errorMessage = "Unknown error";
                
                if (joinError is AggregateException aggregateEx && aggregateEx.InnerExceptions.Count > 0)
                {
                    errorMessage = aggregateEx.InnerExceptions[0].Message;
                }
                else if (joinError != null)
                {
                    errorMessage = joinError.Message;
                }
                
                DebugLog($"Lobby join attempt {attempt+1} failed: {errorMessage}");
                
                if (attempt == maxRetries - 1)
                {
                    OnConnectionResult?.Invoke(false, $"Failed to join lobby: {errorMessage}");
                    yield break;
                }
                
                continue; // Try again
            }
            
            // Check the result of the task
            lobbyJoined = joinTask.Result;
            
            if (lobbyJoined)
            {
                // Success! Don't need further retries
                break;
            }
            else if (attempt == maxRetries - 1)
            {
                OnConnectionResult?.Invoke(false, "Failed to join lobby - connection refused");
                yield break;
            }
        }
        
        // Final result after all retries
        if (!lobbyJoined)
        {
            OnConnectionResult?.Invoke(false, "Failed to join lobby after multiple attempts");
        }
    }

    private void HandleLobbyCreated(Lobby lobby)
    {
        if (_isProcessingLobbyEvent) return;
        _isProcessingLobbyEvent = true;

        try
        {
            DebugLog($"Lobby created: {lobby.Name}, ID: {lobby.Id}, Code: {lobby.LobbyCode}");

            // Start host mode through GameManager
            if (GameManager.Instance != null)
            {
                // Ensure world details are synchronized
                GameManager.Instance.SetWorldDetails(_selectedWorldId, _selectedWorldName, _isMultiplayerWorld);
                
                // Start host mode
                DebugLog("Starting host mode through GameManager");
                GameManager.Instance.StartHostMode();
            }
            else
            {
                Debug.LogError("GameManager not available when handling lobby creation!");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error handling lobby creation: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            _isProcessingLobbyEvent = false;
        }
    }

    private void HandleLobbyJoined(Lobby lobby)
    {
        if (_isProcessingLobbyEvent) return;
        _isProcessingLobbyEvent = true;

        try
        {
            DebugLog($"Joined lobby: {lobby.Name}, ID: {lobby.Id}");

            // Look for the actual relay join code in the lobby data
            string relayJoinCode = null;
            if (lobby.Data != null && lobby.Data.TryGetValue("RelayJoinCode", out var relayCodeData))
            {
                relayJoinCode = relayCodeData.Value;
                DebugLog($"Found Relay join code in lobby data: {relayJoinCode}");
            }
            else
            {
                DebugLog("WARNING: No Relay join code found in lobby data!");
            }

            // Create a client world
            if (WorldSaveManager.Instance != null)
            {
                WorldSaveManager.Instance.InitializeClientWorld(lobby.Name, true);
                string worldId = WorldSaveManager.Instance.CurrentWorldId;

                if (GameManager.Instance != null)
                {
                    // Set world details in GameManager
                    GameManager.Instance.SetWorldDetails(worldId, lobby.Name, true);
                    
                    // Start client mode
                    DebugLog("Starting client mode through GameManager");
                    GameManager.Instance.StartClientMode();
                }
                else
                {
                    Debug.LogError("GameManager not available when handling lobby join!");
                    OnConnectionResult?.Invoke(false, "GameManager not available");
                }
            }
            else
            {
                Debug.LogError("WorldSaveManager not available when handling lobby join!");
                OnConnectionResult?.Invoke(false, "WorldSaveManager not available");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error handling lobby join: {ex.Message}\n{ex.StackTrace}");
            OnConnectionResult?.Invoke(false, $"Error setting up game: {ex.Message}");
        }
        finally
        {
            _isProcessingLobbyEvent = false;
        }
    }

    private void HandleConnectionResult(bool success, string message)
    {
        DebugLog($"Connection result: {(success ? "Success" : "Failure")} - {message}");
        
        // If connection failed, show error or log message
        if (!success)
        {
            Debug.LogError($"Connection failed: {message}");
        }
        
        // Forward the connection result to subscribers
        OnConnectionResult?.Invoke(success, message);
    }

    private void DebugLog(string message)
    {
        if (enableDebugLogs)
        {
            Debug.Log($"[NetworkBridge] {message}");
        }
    }
}