using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

public class MultiplayerManager : MonoBehaviour
{
    private static MultiplayerManager instance;
    public static MultiplayerManager Instance
    {
        get
        {
            if (instance == null)
            {
                if (!Application.isPlaying)
                {
                    instance = FindAnyObjectByType<MultiplayerManager>();

                    if (instance == null)
                    {
                        Debug.LogWarning("MultiplayerManager.Instance requested while not in play mode. Returning null.");
                    }

                    return instance;
                }

                GameObject go = new GameObject("MultiplayerManager");
                instance = go.AddComponent<MultiplayerManager>();
                DontDestroyOnLoad(go);
            }
            return instance;
        }
    }

    // Relay and connection
    private bool isInitialized = false;
    public bool IsInitialized => isInitialized;
    private string joinCode;
    public string JoinCode => joinCode;
    private const int MaxConnections = 10;
    
    // Lobby
    private Lobby currentLobby;
    public string LobbyId => currentLobby?.Id;
    public string LobbyCode => currentLobby?.LobbyCode;
    private float heartbeatTimer;
    private const float HeartbeatInterval = 15f; // Lobby heartbeat interval in seconds
    
    // Event delegates
    public delegate void LobbyEventHandler(Lobby lobby);
    public event LobbyEventHandler OnLobbyCreated;
    public event LobbyEventHandler OnLobbyJoined;
    
    public delegate void ConnectionEventHandler(bool success, string message);
    public event ConnectionEventHandler OnConnectionResult;

    private bool verboseDebugging = true; 
    private readonly object initLock = new object();
    private Task<bool> initializationTask;

    private void Awake()
    {
        VerboseLog("MultiplayerManager Awake called");
        
        if (instance != null && instance != this)
        {
            VerboseLog($"Multiple instances detected, destroying this one (ID: {GetInstanceID()})");
            Destroy(gameObject);
            return;
        }
        
        VerboseLog($"Setting instance to this (ID: {GetInstanceID()})");
        instance = this;
        DontDestroyOnLoad(gameObject);
        
        // Pre-initialize services if possible
        VerboseLog("Attempting early service initialization");
        InitializeServicesAsync().ContinueWith(task => {
            if (task.IsCompletedSuccessfully && task.Result)
            {
                VerboseLog("Early service initialization successful");
            }
            else
            {
                VerboseLog("Early service initialization failed or was incomplete");
            }
        });
    }


    private void Update()
    {
        // Send lobby heartbeats to keep it active
        if (AuthenticationService.Instance.IsSignedIn && currentLobby != null)
        {
            heartbeatTimer -= Time.deltaTime;
            if (heartbeatTimer < 0)
            {
                SendLobbyHeartbeatAsync();
                heartbeatTimer = HeartbeatInterval;
            }
        }
    }

    private void VerboseLog(string message)
    {
        if (verboseDebugging)
        {
            Debug.Log($"[MultiplayerManager-VERBOSE] {message}");
        }
    }

    public bool CheckServicesStatus()
    {
        VerboseLog("CheckServicesStatus called");
        
        bool relayAvailable = false;
        bool authAvailable = false;
        bool lobbyAvailable = false;
        
        try
        {
            authAvailable = AuthenticationService.Instance != null;
            VerboseLog($"Authentication service available: {authAvailable}");
            
            if (authAvailable)
            {
                VerboseLog($"User is signed in: {AuthenticationService.Instance.IsSignedIn}");
                if (AuthenticationService.Instance.IsSignedIn)
                {
                    VerboseLog($"Player ID: {AuthenticationService.Instance.PlayerId}");
                }
            }
        }
        catch (Exception ex)
        {
            VerboseLog($"Error checking Authentication service: {ex.Message}");
        }
        
        try
        {
            relayAvailable = RelayService.Instance != null;
            VerboseLog($"Relay service available: {relayAvailable}");
        }
        catch (Exception ex)
        {
            VerboseLog($"Error checking Relay service: {ex.Message}");
        }
        
        try
        {
            lobbyAvailable = LobbyService.Instance != null;
            VerboseLog($"Lobby service available: {lobbyAvailable}");
        }
        catch (Exception ex)
        {
            VerboseLog($"Error checking Lobby service: {ex.Message}");
        }
        
        bool allServicesAvailable = authAvailable && relayAvailable && lobbyAvailable;
        VerboseLog($"All services available: {allServicesAvailable}");
        
        return allServicesAvailable;
    }

    private void SafeInvokeOnLobbyCreated(Lobby lobby)
    {
        VerboseLog($"SafeInvokeOnLobbyCreated called for lobby: {lobby.Name}, ID: {lobby.Id}");
        
        if (OnLobbyCreated == null)
        {
            VerboseLog("WARNING: No subscribers to OnLobbyCreated event!");
            return;
        }
        
        try
        {
            VerboseLog($"Invoking OnLobbyCreated event with {OnLobbyCreated.GetInvocationList().Length} subscribers");
            OnLobbyCreated.Invoke(lobby);
            VerboseLog("OnLobbyCreated event invoked successfully");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error invoking OnLobbyCreated: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public Task<bool> InitializeServicesAsync()
    {
        lock (initLock)
        {
            VerboseLog("InitializeServicesAsync called");

            if (isInitialized)
            {
                VerboseLog("Services already initialized, returning cached result");
                return Task.FromResult(true);
            }

            if (initializationTask != null)
            {
                if (!initializationTask.IsCompleted)
                {
                    VerboseLog("Initialization already in progress, returning existing task");
                    return initializationTask;
                }

                if (initializationTask.IsCompletedSuccessfully && initializationTask.Result)
                {
                    VerboseLog("Previous initialization task completed successfully");
                    isInitialized = true;
                    return initializationTask;
                }

                VerboseLog("Previous initialization task failed or was cancelled, restarting");
            }

            VerboseLog("Creating new initialization task");
            initializationTask = InitializeServicesInternalAsync();
            return initializationTask;
        }
    }

    private async Task<bool> InitializeServicesInternalAsync()
    {
        bool success = false;
        try
        {
            VerboseLog("Initializing Unity Gaming Services...");

            var options = new InitializationOptions();
            await UnityServices.InitializeAsync(options);
            VerboseLog("UnityServices.InitializeAsync completed successfully");

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                VerboseLog("User not signed in, awaiting anonymous sign-in...");
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                VerboseLog("Anonymous sign-in successful");
            }
            else
            {
                VerboseLog("User already signed in");
            }

            VerboseLog($"Player ID: {AuthenticationService.Instance.PlayerId}");
            isInitialized = true;
            success = true;
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Service initialization failed: {e.Message}\n{e.StackTrace}");
            isInitialized = false;
            return false;
        }
        finally
        {
            if (!success)
            {
                lock (initLock)
                {
                    if (!isInitialized)
                    {
                        initializationTask = null;
                    }
                }
            }
        }
    }

    public Lobby GetCurrentLobby()
    {
        return currentLobby;
    }

    public async Task<bool> CreateLobbyWithRelayAsync(string lobbyName, bool isPrivate)
    {
        VerboseLog($"CreateLobbyWithRelayAsync called for lobby: {lobbyName}, private: {isPrivate}");
        
        if (!isInitialized)
        {
            VerboseLog("Services not initialized, attempting initialization");
            bool initialized = await InitializeServicesAsync();
            if (!initialized)
            {
                Debug.LogError("Failed to initialize services");
                OnConnectionResult?.Invoke(false, "Failed to initialize Unity Gaming Services");
                return false;
            }
            VerboseLog("Services initialized successfully");
        }

        try
        {
            // 1. Create Relay allocation
            VerboseLog("Creating Relay allocation...");
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(MaxConnections);
            VerboseLog($"Allocation created with ID: {allocation.AllocationId}");
            
            // 2. Get join code for the Relay
            VerboseLog("Getting join code for Relay allocation");
            joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            VerboseLog($"Relay join code obtained: {joinCode}");
            
            // 3. Set up the Relay on the NetworkManager's transport
            VerboseLog("Setting up Relay on NetworkManager transport");
            
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport == null)
            {
                Debug.LogError("UnityTransport component not found on NetworkManager");
                OnConnectionResult?.Invoke(false, "UnityTransport component not found on NetworkManager");
                return false;
            }
            
            try
            {
                // Debug transport info before setting relay data
                VerboseLog($"Transport before Relay setup - Address: {transport.ConnectionData.Address}, Port: {transport.ConnectionData.Port}");
                
                VerboseLog("Setting relay server data");
                // Set relay server data for the transport
                transport.SetRelayServerData(
                    allocation.RelayServer.IpV4,
                    (ushort)allocation.RelayServer.Port,
                    allocation.AllocationIdBytes,
                    allocation.Key,
                    allocation.ConnectionData
                );
                
                // Debug transport info after setting relay data
                VerboseLog($"Transport after Relay setup - Address: {transport.ConnectionData.Address}, Port: {transport.ConnectionData.Port}");
                VerboseLog("Relay server data set on transport successfully");
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to set Relay server data: {e.Message}\n{e.StackTrace}");
                OnConnectionResult?.Invoke(false, $"Failed to set up Relay: {e.Message}");
                return false;
            }
            
            // 4. Create a lobby with the Relay join code
            try
            {
                VerboseLog("Creating lobby with Relay join code");
                int maxPlayers = Math.Min(MaxLobbyPlayers, MaxConnections);
                
                // IMPORTANT: Use the Relay-provided join code as both the relay code AND the custom code
                // This ensures the code used for joining is consistent
                string customLobbyCode = joinCode;
                VerboseLog($"Using Relay join code as custom lobby code: {customLobbyCode}");
                
                // Create lobby data with Relay join code
                CreateLobbyOptions lobbyOptions = new CreateLobbyOptions
                {
                    IsPrivate = isPrivate,
                    Player = GetLocalLobbyPlayer(),
                    // Add a custom ID field that we can use to join
                    Data = new Dictionary<string, DataObject>
                    {
                        // Store the relay join code in the lobby for clients to use
                        { "RelayJoinCode", new DataObject(
                            visibility: DataObject.VisibilityOptions.Member,
                            value: joinCode
                        )},
                        // Use the same join code for the custom code field for consistency
                        { "CustomLobbyCode", new DataObject(
                            visibility: DataObject.VisibilityOptions.Public,
                            value: customLobbyCode
                        )}
                    }
                };
                
                // Create the lobby
                VerboseLog($"Calling LobbyService.CreateLobbyAsync for lobby: {lobbyName}, maxPlayers: {maxPlayers}");
                currentLobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, maxPlayers, lobbyOptions);
                
                // Log the success details
                VerboseLog($"Lobby created successfully: {currentLobby.Name}, ID: {currentLobby.Id}");
                VerboseLog($"Unity lobby code: {currentLobby.LobbyCode}");
                VerboseLog($"Custom lobby code (same as join code): {joinCode}");
                
                // Start heartbeat timer
                heartbeatTimer = HeartbeatInterval;
                
                // Notify listeners
                VerboseLog("Invoking OnLobbyCreated event");
                SafeInvokeOnLobbyCreated(currentLobby);
                
                VerboseLog("Invoking OnConnectionResult event with success");
                OnConnectionResult?.Invoke(true, "Lobby created successfully");
                
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Lobby creation failed: {e.Message}\n{e.StackTrace}");
                OnConnectionResult?.Invoke(false, $"Lobby creation failed: {e.Message}");
                return false;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to create Relay allocation: {e.Message}\n{e.StackTrace}");
            OnConnectionResult?.Invoke(false, $"Failed to create Relay allocation: {e.Message}");
            return false;
        }
    }

    private string GenerateRandomLobbyCode()
    {
        // Based on testing and error messages, Unity's Relay only accepts 
        // uppercase letters A-F for lobby codes. No numbers, no other letters.
        const string chars = "ABCDEF";
        var random = new System.Random();
        return new string(Enumerable.Repeat(chars, 6)
            .Select(s => s[random.Next(s.Length)]).ToArray());
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
                VerboseLog($"Invalid character in lobby code: '{c}' (U+{(int)c:X4})");
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

        // Remove any single quotes, spaces, and other potentially problematic characters
        string cleaned = joinCode.Replace("'", "").Replace(" ", "");
        
        // Log detailed information about the code to help with debugging
        VerboseLog($"Original join code: '{joinCode}', Cleaned: '{cleaned}'");
        
        // Basic validation check - log warning if the code contains potentially problematic characters
        foreach (char c in cleaned)
        {
            if (!char.IsLetterOrDigit(c))
            {
                VerboseLog($"WARNING: Join code contains non-alphanumeric character: '{c}' (U+{(int)c:X4})");
            }
        }
        
        return cleaned;
    }

    public async Task<bool> JoinLobbyByCodeAsync(string lobbyCode)
    {
        if (!isInitialized)
        {
            bool initialized = await InitializeServicesAsync();
            if (!initialized) 
            {
                OnConnectionResult?.Invoke(false, "Failed to initialize Unity Gaming Services");
                return false;
            }
        }

        int maxRetries = 3;
        float retryDelay = 1.0f;
        
        // Clean the lobby code and ensure consistent casing
        string cleanedLobbyCode = CleanJoinCode(lobbyCode).ToUpperInvariant();
        if (string.IsNullOrEmpty(cleanedLobbyCode))
        {
            Debug.LogError("[Multiplayer] Join code is empty after cleaning");
            OnConnectionResult?.Invoke(false, "Invalid join code format");
            return false;
        }
        
        Debug.Log($"[Multiplayer] Attempting to join lobby with code: {cleanedLobbyCode}");

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    Debug.Log($"[Multiplayer] Retry attempt {attempt+1}/{maxRetries} joining lobby with code {cleanedLobbyCode}");
                    // Wait before retry with exponential backoff
                    await Task.Delay(Mathf.RoundToInt(retryDelay * 1000));
                    retryDelay *= 2;
                }
                
                // Ensure authentication is fresh
                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    Debug.Log("[Multiplayer] Re-authenticating before joining lobby");
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                }

                // FIRST APPROACH: Try joining with the code directly as a Unity lobby code
                try
                {
                    JoinLobbyByCodeOptions options = new JoinLobbyByCodeOptions
                    {
                        Player = GetLocalLobbyPlayer()
                    };
                    
                    Debug.Log($"[Multiplayer] First attempt: Joining using standard Unity lobby code: {cleanedLobbyCode}");
                    currentLobby = await LobbyService.Instance.JoinLobbyByCodeAsync(cleanedLobbyCode, options);
                    Debug.Log($"[Multiplayer] Joined lobby: {currentLobby.Name}, ID: {currentLobby.Id}");
                    
                    // Successfully joined the lobby, now join the Relay
                    if (await ConfigureRelayForJoinedLobby())
                    {
                        return true; // Success!
                    }
                    else
                    {
                        // Failed to join Relay, leave the lobby and try different approach
                        await LeaveLobbyAsync();
                    }
                }
                catch (Exception e)
                {
                    Debug.Log($"[Multiplayer] Standard lobby code join failed: {e.Message}, trying query approach");
                    // Continue to the second approach
                }

                // SECOND APPROACH: Query for lobbies and search for custom lobby code
                try
                {
                    Debug.Log($"[Multiplayer] Second attempt: Searching for lobby with custom code: {cleanedLobbyCode}");
                    
                    // We can't directly query for custom data fields, so we'll get all lobbies and filter manually
                    QueryLobbiesOptions queryOptions = new QueryLobbiesOptions
                    {
                        Count = 25,
                        Filters = new List<QueryFilter>
                        {
                            // Only get lobbies with available slots
                            new QueryFilter(
                                field: QueryFilter.FieldOptions.AvailableSlots,
                                value: "0", 
                                op: QueryFilter.OpOptions.GT)
                        }
                    };

                    QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(queryOptions);
                    
                    // Manually find lobbies with matching custom code
                    Lobby matchingLobby = null;
                    foreach (var lobby in response.Results)
                    {
                        if (lobby.Data != null && 
                            lobby.Data.TryGetValue("CustomLobbyCode", out var customCodeData) &&
                            string.Equals(customCodeData.Value, cleanedLobbyCode, StringComparison.OrdinalIgnoreCase))
                        {
                            matchingLobby = lobby;
                            Debug.Log($"[Multiplayer] Found matching lobby: {lobby.Name}, ID: {lobby.Id}");
                            break;
                        }
                    }
                    
                    if (matchingLobby != null)
                    {
                        // Join the found lobby
                        JoinLobbyByIdOptions joinOptions = new JoinLobbyByIdOptions
                        {
                            Player = GetLocalLobbyPlayer()
                        };
                        
                        currentLobby = await LobbyService.Instance.JoinLobbyByIdAsync(matchingLobby.Id, joinOptions);
                        Debug.Log($"[Multiplayer] Joined lobby: {currentLobby.Name}, ID: {currentLobby.Id}");
                        
                        // Configure and join Relay
                        if (await ConfigureRelayForJoinedLobby())
                        {
                            return true; // Success!
                        }
                        else
                        {
                            // Failed to join Relay, leave the lobby
                            await LeaveLobbyAsync();
                        }
                    }
                    else
                    {
                        Debug.Log($"[Multiplayer] No lobbies found with custom code {cleanedLobbyCode}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Multiplayer] Query approach failed: {e.Message}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Multiplayer] Error in JoinLobbyByCodeAsync: {e.GetType().Name}: {e.Message}");
            }
        }
        
        // If we get here, all retries failed
        OnConnectionResult?.Invoke(false, "Failed to join lobby after multiple attempts");
        return false;
    }

    private async Task<bool> ConfigureRelayForJoinedLobby()
    {
        if (currentLobby == null)
        {
            Debug.LogError("[Multiplayer] No current lobby when configuring Relay");
            return false;
        }
        
        // First try to get the Relay join code from the lobby data
        string relayJoinCode = null;
        
        // Try RelayJoinCode first (our primary storage location)
        if (currentLobby.Data != null && currentLobby.Data.TryGetValue("RelayJoinCode", out var relayJoinCodeData))
        {
            relayJoinCode = relayJoinCodeData.Value;
            Debug.Log($"[Multiplayer] Found Relay join code in lobby: {relayJoinCode}");
        }
        // Try LobbyCode as fallback
        else if (!string.IsNullOrEmpty(currentLobby.LobbyCode))
        {
            relayJoinCode = currentLobby.LobbyCode;
            Debug.Log($"[Multiplayer] Using lobby code as relay code: {relayJoinCode}");
        }
        // Try CustomLobbyCode as last resort
        else if (currentLobby.Data != null && currentLobby.Data.TryGetValue("CustomLobbyCode", out var customCodeData))
        {
            relayJoinCode = customCodeData.Value;
            Debug.Log($"[Multiplayer] Using custom lobby code as relay code: {relayJoinCode}");
        }
        
        // Validate and join Relay if we have a code
        if (!string.IsNullOrEmpty(relayJoinCode))
        {
            bool relayJoined = await JoinRelayAsync(relayJoinCode);
            if (relayJoined)
            {
                OnLobbyJoined?.Invoke(currentLobby);
                OnConnectionResult?.Invoke(true, "Joined lobby and Relay successfully");
                return true;
            }
            else
            {
                Debug.LogError($"[Multiplayer] Failed to join Relay with code: {relayJoinCode}");
            }
        }
        else
        {
            Debug.LogError("[Multiplayer] No Relay join code found in lobby data");
        }
        
        return false;
    }

    public async Task<bool> JoinRelayAsync(string relayJoinCode)
    {
        if (!isInitialized)
        {
            bool initialized = await InitializeServicesAsync();
            if (!initialized) return false;
        }

        try
        {
            Debug.Log($"[Multiplayer] Joining Relay with code: {relayJoinCode}");
            
            // Join the Relay allocation
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);
            
            // Set up the Relay on the NetworkManager's transport
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport == null)
            {
                Debug.LogError("[Multiplayer] UnityTransport component not found on NetworkManager");
                return false;
            }
            
            // Set the relay server data with individual parameters
            transport.SetRelayServerData(
                joinAllocation.RelayServer.IpV4,
                (ushort)joinAllocation.RelayServer.Port,
                joinAllocation.AllocationIdBytes,
                joinAllocation.Key,
                joinAllocation.ConnectionData,
                joinAllocation.HostConnectionData
            );
            
            Debug.Log("[Multiplayer] Relay joined successfully");
            this.joinCode = relayJoinCode;
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[Multiplayer] Failed to join Relay: {e.Message}\n{e.StackTrace}");
            return false;
        }
    }

    public async Task LeaveLobbyAsync()
    {
        if (currentLobby != null && AuthenticationService.Instance.IsSignedIn)
        {
            try
            {
                await LobbyService.Instance.RemovePlayerAsync(currentLobby.Id, AuthenticationService.Instance.PlayerId);
                Debug.Log($"[Multiplayer] Left lobby: {currentLobby.Name}");
                currentLobby = null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Multiplayer] Error leaving lobby: {e.Message}");
            }
        }
    }

    public async Task<List<Lobby>> ListLobbiesAsync(int maxResults = 25)
    {
        if (!isInitialized)
        {
            bool initialized = await InitializeServicesAsync();
            if (!initialized) return new List<Lobby>();
        }

        try
        {
            QueryLobbiesOptions options = new QueryLobbiesOptions
            {
                Count = maxResults,
                Filters = new List<QueryFilter>
                {
                    // Only show lobbies that are not private
                    new QueryFilter(
                        field: QueryFilter.FieldOptions.AvailableSlots,
                        op: QueryFilter.OpOptions.GT,
                        value: "0")
                },
                Order = new List<QueryOrder>
                {
                    // Sort by newest lobbies first
                    new QueryOrder(
                        asc: false,
                        field: QueryOrder.FieldOptions.Created)
                }
            };

            QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(options);
            Debug.Log($"[Multiplayer] Found {response.Results.Count} public lobbies");
            return response.Results;
        }
        catch (Exception e)
        {
            Debug.LogError($"[Multiplayer] Error listing lobbies: {e.Message}");
            return new List<Lobby>();
        }
    }

    public async Task<Lobby> RefreshLobbyAsync()
    {
        if (currentLobby == null) return null;

        try
        {
            currentLobby = await LobbyService.Instance.GetLobbyAsync(currentLobby.Id);
            return currentLobby;
        }
        catch (Exception e)
        {
            Debug.LogError($"[Multiplayer] Error refreshing lobby: {e.Message}");
            return null;
        }
    }

    private async void SendLobbyHeartbeatAsync()
    {
        if (currentLobby == null || !AuthenticationService.Instance.IsSignedIn) return;

        try
        {
            await LobbyService.Instance.SendHeartbeatPingAsync(currentLobby.Id);
            Debug.Log($"[Multiplayer] Sent heartbeat for lobby {currentLobby.Id}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Multiplayer] Error sending lobby heartbeat: {e.Message}");
        }
    }

    private Player GetLocalLobbyPlayer()
    {
        return new Player
        {
            Data = new Dictionary<string, PlayerDataObject>
            {
                // You can add player-specific data here
                { "PlayerName", new PlayerDataObject(
                    visibility: PlayerDataObject.VisibilityOptions.Member,
                    value: $"Player_{AuthenticationService.Instance.PlayerId.Substring(0, 5)}")
                }
            }
        };
    }

    private void OnDestroy()
    {
        if (currentLobby != null && AuthenticationService.Instance.IsSignedIn)
        {
            // Try to leave the lobby
            LobbyService.Instance.RemovePlayerAsync(
                currentLobby.Id,
                AuthenticationService.Instance.PlayerId).ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        Debug.Log($"[Multiplayer] Left lobby on manager destroy: {currentLobby.Id}");
                    }
                });
        }
    }

    // Define this constant to match the class
    private const int MaxLobbyPlayers = 10;
}