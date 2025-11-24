# Duplicate Event Subscriptions Fix

## Problem

UI events were firing multiple times, causing duplicate log entries and potentially multiple callbacks executing for the same event. The logs showed:
- "Ensuring exclusivity for panel: StartGamePanel" appearing 3 times
- "[Start Game] Loading worlds..." appearing 3 times  
- World list loading 3 times with identical results

## Root Cause

**Multiple Event Subscriptions in `GameUIManager`:**

The `GameUIManager` was subscribing to `MultiplayerManager` events in TWO places:

1. **`InitializeNetworkServices()`** (called from `Awake`):
```csharp
MultiplayerManager.Instance.OnConnectionResult += HandleConnectionResult;
MultiplayerManager.Instance.OnLobbyCreated += HandleLobbyCreated;
MultiplayerManager.Instance.OnLobbyJoined += HandleLobbyJoined;
```

2. **`OnEnable()`**:
```csharp
MultiplayerManager.Instance.OnConnectionResult += HandleConnectionResult;
MultiplayerManager.Instance.OnLobbyCreated += HandleLobbyCreated;
MultiplayerManager.Instance.OnLobbyJoined += HandleLobbyJoined;
```

**Problem:** `OnEnable()` is called every time:
- The GameObject is enabled
- The scene loads
- The GameObject is re-enabled after being disabled

Each call adds NEW subscriptions **without removing old ones**, resulting in duplicate callbacks.

## Solution

**Unsubscribe before subscribing (defensive subscription pattern):**

```csharp
// In OnEnable():
// Unsubscribe first to prevent duplicate subscriptions
MultiplayerManager.Instance.OnConnectionResult -= HandleConnectionResult;
MultiplayerManager.Instance.OnLobbyCreated -= HandleLobbyCreated;
MultiplayerManager.Instance.OnLobbyJoined -= HandleLobbyJoined;
// Now subscribe
MultiplayerManager.Instance.OnConnectionResult += HandleConnectionResult;
MultiplayerManager.Instance.OnLobbyCreated += HandleLobbyCreated;
MultiplayerManager.Instance.OnLobbyJoined += HandleLobbyJoined;

// In InitializeNetworkServices():
// Connect event handlers (unsubscribe first to prevent duplicates)
MultiplayerManager.Instance.OnConnectionResult -= HandleConnectionResult;
MultiplayerManager.Instance.OnLobbyCreated -= HandleLobbyCreated;
MultiplayerManager.Instance.OnLobbyJoined -= HandleLobbyJoined;
MultiplayerManager.Instance.OnConnectionResult += HandleConnectionResult;
MultiplayerManager.Instance.OnLobbyCreated += HandleLobbyCreated;
MultiplayerManager.Instance.OnLobbyJoined += HandleLobbyJoined;
```

**Note:** In C#, unsubscribing (`-=`) is safe even if not currently subscribed - it simply does nothing.

## Why This Pattern is Better

1. **Idempotent**: Can be called multiple times safely
2. **Defensive**: Prevents duplicate subscriptions even if called unexpectedly
3. **Matches existing pattern**: The code already used this pattern for `NetworkConnectionBridge` (lines 143-144)

## Evidence from Logs

**Before Fix:**
```
Ensuring exclusivity for panel: StartGamePanel
[Start Game] Loading worlds...
[Start Game] Found 7 worlds
Ensuring exclusivity for panel: StartGamePanel  ← DUPLICATE
[Start Game] Loading worlds...  ← DUPLICATE
[Start Game] Found 7 worlds  ← DUPLICATE
Ensuring exclusivity for panel: StartGamePanel  ← DUPLICATE
[Start Game] Loading worlds...  ← DUPLICATE
[Start Game] Found 7 worlds  ← DUPLICATE
```

**After Fix (expected):**
```
Ensuring exclusivity for panel: StartGamePanel
[Start Game] Loading worlds...
[Start Game] Found 7 worlds
```

## Files Modified

- `Assets/Scripts/UI/GameUIManager.cs` - Added defensive unsubscribe before subscribe in both `OnEnable()` and `InitializeNetworkServices()`

## Related Issues

This fix resolves:
- Duplicate UI callbacks
- Multiple identical log entries
- Potential performance issues from redundant event processing
- Confusing debugging experience with repeated messages




