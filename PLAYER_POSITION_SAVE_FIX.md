# Player Position Save Fix

## Problem
Player position was not being saved consistently during gameplay. Players would spawn at seemingly random positions when loading into a world - sometimes at the initial spawn position, sometimes at a different location.

### Root Causes Identified

#### 1. Unity Editor Specific Issue
When clicking the Stop button in the Unity Editor, the application quit sequence happens very quickly and player positions weren't being saved before NetworkManager shut down.

#### 2. CRITICAL: PlayerSpawner Registration Timing Issue (Main Cause)
**PlayerSpawner was registering its connection callbacks AFTER players had already spawned!**

The problem sequence:
1. `GameManager` calls `NetworkManager.StartHost()`
2. Player spawns immediately (client connects)
3. Gameplay scene loads
4. **PlayerSpawner.Start()** finally runs and registers connection callbacks
5. `OnClientConnected` never fires because client is already connected
6. Saved position is never loaded!

This is why saves worked but loads didn't - the load code simply never ran.

### Root Cause
Player positions were only being saved in the following scenarios:
1. When the player initially spawned
2. When the player teleported
3. When the player disconnected from the game

**The player position was NOT being saved during normal gameplay movement.** This meant if a player walked around and then:
- The game crashed
- The game closed unexpectedly
- The world was reloaded
- The game restarted

They would spawn at their last *saved* position, which could be:
- The initial spawn position (if they never teleported or disconnected)
- A position from a previous teleport
- A position from a previous session

## Solution

### 1. Fixed PlayerSpawner Timing Issue + Spawn Safety Bypass (CRITICAL FIXES)
**Files:** `Assets/Scripts/Network/PlayerSpawner.cs` + `Assets/Scripts/Network/NetworkPlayerSetup.cs`

**The main fixes:** 
1. PlayerSpawner now checks for already-connected clients when it starts, and manually triggers position loading for them
2. NetworkPlayerSetup now skips spawn safety when a player was teleported to a saved position

**Changes:**
```csharp
// In Start() method - after registering callbacks
// CRITICAL: Check for already-connected clients
if (NetworkManager.Singleton.IsServer)
{
    Debug.Log("[PlayerSpawner] Checking for already-connected clients...");
    foreach (var clientId in NetworkManager.Singleton.ConnectedClientsIds)
    {
        Debug.Log($"[PlayerSpawner] Found already-connected client: {clientId}");
        // Manually trigger OnClientConnected for already-connected clients
        OnClientConnected(clientId);
    }
}

// In OnClientConnected() - detect already-spawned players and teleport them
GameObject existingPlayerObj = FindPlayerObject(clientId);
if (existingPlayerObj != null)
{
    Debug.Log($"[PlayerSpawner] Found EXISTING player object for client {clientId}, applying saved position");
    
    // Try to load saved position
    Vector3 savedPosition = GetClientLastPosition(clientId);
    if (savedPosition != Vector3.zero && loadPreviousPosition)
    {
        Debug.Log($"[PlayerSpawner] Teleporting existing player {clientId} to saved position: {savedPosition}");
        TeleportPlayer(clientId, existingPlayerObj, savedPosition);
        clientPositionLoaded[clientId] = true;
        pendingSpawnPositions[clientId] = savedPosition;
    }
    
    // Track the player object
    playerObjects[clientId] = existingPlayerObj;
    return;
}
```

**How it works:**
- When PlayerSpawner starts, it now checks `NetworkManager.ConnectedClientsIds`
- For any already-connected clients, it manually calls `OnClientConnected()`
- `OnClientConnected()` now checks if the player object already exists
- If the player exists AND there's a saved position, it immediately teleports them
- **CRITICAL:** It also marks them as `clientTeleportedToSavedPosition[clientId] = true`
- NetworkPlayerSetup checks this flag and **skips spawn safety** if true
- This prevents spawn safety from overwriting the saved position with (0, 30, 0)

**Additional PlayerSpawner changes:**
```csharp
// Added tracking dictionary
private Dictionary<ulong, bool> clientTeleportedToSavedPosition = new Dictionary<ulong, bool>();

// Public method for NetworkPlayerSetup to check
public bool WasPlayerTeleportedToSavedPosition(ulong clientId)
{
    return clientTeleportedToSavedPosition.TryGetValue(clientId, out bool wasTeleported) && wasTeleported;
}
```

**NetworkPlayerSetup changes:**
```csharp
// Before running spawn safety, check if player was teleported
bool wasTeleportedToSavedPosition = false;
if (PlayerSpawner.Instance != null)
{
    wasTeleportedToSavedPosition = PlayerSpawner.Instance.WasPlayerTeleportedToSavedPosition(OwnerClientId);
    if (wasTeleportedToSavedPosition)
    {
        DebugLog($"Player was teleported to saved position, SKIPPING spawn safety");
    }
}

// Only run spawn safety if NOT teleported to saved position
if (enableSpawnSafety && spawnSafety != null && !wasTeleportedToSavedPosition)
{
    // Run spawn safety...
}
```

### 2. Periodic Position Saving in NetworkManagerHelper
**File:** `Assets/Scripts/Network/NetworkManagerHelper.cs`

Added periodic player position saving to disk and proper shutdown handling:
- **New configurable interval:** `playerPositionSaveInterval` (default: 10 seconds)
- **New tracking dictionary:** `lastPositionSaveTime` tracks when each player's position was last saved
- **Updated `UpdateAllPlayerPositions()`:** Now saves player positions to disk every 10 seconds during normal gameplay
- **Updated cleanup:** Properly cleans up save time tracking on disconnect

**How it works:**
- The `UpdateAllPlayerPositions()` method already runs every 2 seconds on the server
- It already tracks player movements for chunk loading purposes
- Now it also checks if 10 seconds have passed since the last save for each player
- If so, it calls `PlayerSpawner.SavePlayerPosition()` which saves to both PlayerPrefs and WorldSaveManager

**Changes:**
```csharp
// Added new fields
[SerializeField] private float playerPositionSaveInterval = 10f;
private Dictionary<ulong, float> lastPositionSaveTime = new Dictionary<ulong, float>();

// Added to UpdateAllPlayerPositions()
// Periodically save position to disk (separate from position updates)
if (!lastPositionSaveTime.TryGetValue(clientId, out float lastSaveTime) || 
    Time.time - lastSaveTime >= playerPositionSaveInterval)
{
    if (PlayerSpawner.Instance != null)
    {
        PlayerSpawner.Instance.SavePlayerPosition(clientId, currentPos);
        lastPositionSaveTime[clientId] = Time.time;
    }
}

// Added OnApplicationQuit() handler
private void OnApplicationQuit()
{
    Debug.Log("[NetworkManagerHelper] Application quitting - saving all player positions");
    SaveAllPlayerPositions();
}

// Added SaveAllPlayerPositions() helper method
private void SaveAllPlayerPositions()
{
    if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        return;
        
    if (PlayerSpawner.Instance == null)
        return;
    
    foreach (var entry in trackedPlayers)
    {
        ulong clientId = entry.Key;
        GameObject playerObj = entry.Value;
        
        if (playerObj != null)
        {
            PlayerSpawner.Instance.SavePlayerPosition(clientId, playerObj.transform.position);
            Debug.Log($"[NetworkManagerHelper] Final save for player {clientId}");
        }
    }
}

// Updated OnDestroy() to save positions before cleanup
private void OnDestroy()
{
    SaveAllPlayerPositions(); // Save before destroying
    // ... rest of cleanup
}
```

### 2. Save Player Positions During World Save
**File:** `Assets/Scripts/TerrainGen/WorldSaveManager.cs`

Enhanced the `SaveWorld()` method to save all player positions when the world is saved (auto-save or manual save).

**How it works:**
- When `SaveWorld()` is called (every 5 minutes by default, or on application quit)
- It now iterates through all spawned player network objects
- Saves each player's current position using `PlayerSpawner.SavePlayerPosition()`

**Changes:**
```csharp
// Added to SaveWorld() method
// Save all player positions if we're on the server
if (NetworkManager.Singleton != null && 
    NetworkManager.Singleton.IsServer &&
    PlayerSpawner.Instance != null)
{
    foreach (var netObj in NetworkManager.Singleton.SpawnManager.SpawnedObjects.Values)
    {
        if (netObj.IsPlayerObject)
        {
            ulong clientId = netObj.OwnerClientId;
            Vector3 position = netObj.transform.position;
            PlayerSpawner.Instance.SavePlayerPosition(clientId, position);
        }
    }
}
```

### 3. Save and Disconnect Button in Gameplay UI
**File:** `Assets/Scripts/UI/GameUIManager.cs`

Added a "Save and Disconnect" button to the gameplay menu for testing and proper game exit.

**How it works:**
- Press ESC during gameplay to open the gameplay menu
- Click "Save and Disconnect" to explicitly save the world and all player positions
- Disconnects from the network and returns to the main menu
- Useful for testing in Unity Editor and as a proper "Exit to Menu" option for players

**Changes:**
```csharp
// Added new button references
[Header("Gameplay Menu")]
[SerializeField] private Button saveAndDisconnectButton;
[SerializeField] private Button resumeGameButton;

// Added SaveAndDisconnect() method
private void SaveAndDisconnect()
{
    // Save the world and all player positions
    if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
    {
        WorldSaveManager.Instance.SaveWorld();
    }
    
    // Disconnect from network
    if (NetworkManager.Singleton != null)
    {
        NetworkManager.Singleton.Shutdown();
    }
    
    // Return to main menu
    StartCoroutine(ReturnToMainMenuAfterDelay(0.5f));
}
```

## Coverage

The fix now ensures player positions are saved in ALL scenarios:

### Regular Gameplay (NEW ✅)
- **Periodic saves every 10 seconds** during normal movement
- Configurable interval via `playerPositionSaveInterval` in NetworkManagerHelper

### World Save Events (NEW ✅)
- **Auto-save** (default: every 5 minutes)
- **Manual save** (if implemented in UI)
- **Application quit** (calls SaveWorld)

### Shutdown/Quit Events (NEW ✅)
- **Unity Editor Stop button** - now saves before NetworkManager shuts down
- **Application.Quit()** - properly saves via OnApplicationQuit handler
- **OnDestroy** - saves during cleanup

### UI-Triggered Events (NEW ✅)
- **Save and Disconnect button** - explicit save via gameplay menu

### Already Working Events
- **Initial spawn** - saves position when player first spawns
- **Teleport** - saves position when player teleports
- **Disconnect** - saves position when player disconnects

## Testing Recommendations

### Unity Editor Tests (Important!)

1. **Editor Stop Button Test:**
   - Start a world in Play mode
   - Walk around for 15+ seconds
   - Click the Stop button in Unity Editor
   - Enter Play mode again and load the same world
   - ✅ Should spawn at approximately where you were (within 10 seconds of walking)

2. **Save and Disconnect Button Test:**
   - Start a world in Play mode
   - Walk to a specific location
   - Press ESC to open the gameplay menu
   - Click "Save and Disconnect"
   - Enter Play mode again and load the same world
   - ✅ Should spawn exactly where you disconnected

### Runtime Tests

3. **Basic Test:**
   - Start a world, walk around for 15+ seconds
   - Quit the game normally
   - Reload the world
   - ✅ Should spawn at approximately where you were (within 10 seconds of walking)

4. **Periodic Save Test:**
   - Start a world, note your spawn position
   - Walk in one direction for 30 seconds
   - Wait at your destination for 5 seconds (ensure save happens)
   - Quit and reload
   - ✅ Should spawn at your destination, not the original spawn

5. **Auto-Save Test:**
   - Walk around continuously for 6+ minutes (longer than auto-save interval)
   - Force quit the game (Alt+F4 or kill process)
   - Reload the world
   - ✅ Should spawn within the last 5 minutes of movement

6. **Multiplayer Test:**
   - Host a game, have 2+ players join
   - All players walk to different locations
   - Wait 15 seconds
   - Disconnect and reconnect each player individually
   - ✅ Each player should spawn where they were, not at the default spawn

## Configuration

You can adjust the save interval in the Unity Inspector:
- Select the GameObject with `NetworkManagerHelper` component
- Find "Player Position Save Interval" field
- Default: 10 seconds
- Recommended range: 5-30 seconds
  - Lower = more frequent saves, better accuracy, more disk I/O
  - Higher = less frequent saves, less accurate, less disk I/O

## Unity Editor Setup

To use the Save and Disconnect button:

1. Open the GameplayScene in Unity
2. Find the Canvas with GameUIManager component
3. Locate the GameplayContainer (should be a child or referenced object)
4. Add two buttons to the GameplayContainer:
   - **"Save and Disconnect"** button → Assign to `saveAndDisconnectButton` field in GameUIManager
   - **"Resume Game"** button → Assign to `resumeGameButton` field in GameUIManager
5. The buttons will automatically be wired up through the `SetupButtonListeners()` method

If you don't want to create UI elements, the automatic save systems still work:
- Periodic saves every 10 seconds
- OnApplicationQuit saves
- OnDestroy saves

## Notes

- The fix works for both single-player (host) and multiplayer games
- Host player position is tracked and saved like any other player
- The save operation is lightweight (JSON file write) and shouldn't impact performance
- Position is saved to two locations for redundancy:
  1. **PlayerPrefs** - for backward compatibility
  2. **WorldSaveManager** - per-world save files in `Application.persistentDataPath/Worlds/[WorldId]/Players/`
- Unity Editor Stop button now properly saves positions via OnApplicationQuit and OnDestroy handlers
- The execution order ensures NetworkManagerHelper saves positions BEFORE NetworkManager shuts down

## Files Modified

1. **`Assets/Scripts/Network/PlayerSpawner.cs`** ⭐ CRITICAL FIX #1
   - **Fixed timing issue:** Now checks for already-connected clients in Start()
   - **Enhanced OnClientConnected():** Detects and teleports already-spawned players to saved positions
   - **Added tracking:** Dictionary to track which clients were teleported to saved positions
   - **Added public method:** `WasPlayerTeleportedToSavedPosition()` for other components to check
   - **Added extensive logging:** Debug messages to track position load process

2. **`Assets/Scripts/Network/NetworkPlayerSetup.cs`** ⭐ CRITICAL FIX #2
   - **Added spawn safety bypass:** Checks with PlayerSpawner before running spawn safety
   - **Prevents position override:** Skips spawn safety if player was teleported to saved position
   - This was preventing the loaded position from sticking - spawn safety was overwriting it!

3. `Assets/Scripts/Network/NetworkManagerHelper.cs`
   - Added periodic position saving mechanism (every 10 seconds)
   - Added save time tracking dictionary
   - Added OnApplicationQuit() handler for proper shutdown
   - Added SaveAllPlayerPositions() helper method
   - Updated OnDestroy() to save positions before cleanup

4. `Assets/Scripts/TerrainGen/WorldSaveManager.cs`
   - Enhanced SaveWorld() to save all player positions
   - Ensures positions are saved during auto-save and application quit
   - Added extensive logging for debugging

5. `Assets/Scripts/UI/GameUIManager.cs`
   - Added Save and Disconnect button functionality
   - Added Resume Game button functionality
   - Implements proper disconnect sequence with world save
   - Returns player to main menu after disconnect

