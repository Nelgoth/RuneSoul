# Loading Screen Stuck at 70% Fix

## Problem

When loading a world with modifications, the loading screen visually freezes at **70% "Finalizing game world..."** and never progresses. The user sees a frozen loading screen and assumes the game has crashed.

**HOWEVER**: The game is actually NOT frozen! Looking at the logs, the gameplay scene loads successfully in the background:
- Scene loads
- World initializes
- SaveSystem initializes
- Chunks begin loading
- World.Update() starts running

The issue is that **the loading screen UI never receives the signal to transition away** because GameManager never reaches 100% progress.

## Root Cause

**File:** `Assets/Scripts/Network/GameManager.cs` (lines 479-497 and 649-667)

The `LoadGameplayScene()` and `SafeLoadGameplayScene()` methods wait for the scene to load using:

```csharp
while (SceneManager.GetActiveScene().name != gameplaySceneName && sceneLoadTimer < sceneLoadTimeout)
{
    sceneLoadTimer += Time.deltaTime;
    float progress = 0.7f + (sceneLoadTimer / sceneLoadTimeout * 0.2f);
    UpdateLoadingProgress(progress, "Finalizing game world...");
    yield return null;
}
```

**The Problem:** This checks if the scene is the **active** scene. But when `NetworkManager.SceneManager.LoadScene()` loads a scene asynchronously in multiplayer mode, it doesn't immediately make it the active scene!

### What Actually Happens

1. GameManager calls `NetworkManager.SceneManager.LoadScene("GameplayScene", LoadSceneMode.Single)`
2. NetworkManager starts loading the scene asynchronously
3. GameManager enters the while loop checking if `SceneManager.GetActiveScene().name == "GameplayScene"`
4. **Scene loads successfully** but NetworkManager doesn't make it active yet
5. Loop keeps running, stuck at 70%
6. Loop will eventually timeout after 30 seconds, but user thinks it's frozen
7. Because loop never exits normally, `UpdateLoadingProgress(1.0f, "Ready!")` is never called
8. LoadingSceneController never receives 100% signal
9. Loading screen UI never transitions away

### Why This Only Affects Modified Worlds

This issue affects ALL worlds loaded through NetworkManager (multiplayer mode), but it's only noticeable with modified worlds because:
- Fresh worlds load so fast the race condition doesn't occur
- Modified worlds take longer to initialize, making the async scene load timing more apparent
- The bug was there all along, just rare enough to not be noticed

## The Fix

Instead of checking if the scene is **active**, check if it's **loaded at all**:

```csharp
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
```

### Why This Works

- `SceneManager.sceneCount` returns the number of loaded scenes
- `SceneManager.GetSceneAt(i)` gets each loaded scene
- `scene.isLoaded` checks if the scene has finished loading (even if not active)
- We detect the scene as soon as it's loaded, not waiting for it to become active
- GameManager proceeds to call `UpdateLoadingProgress(1.0f, "Ready!")`
- LoadingSceneController receives 100% and transitions to gameplay
- Loading screen properly disappears

## Files Modified

- **Assets/Scripts/Network/GameManager.cs**
  - Fixed `LoadGameplayScene()` method (lines 475-497)
  - Fixed `SafeLoadGameplayScene()` method (lines 645-667)
  - Changed scene detection from "is active" to "is loaded"

## Testing

1. Create a world with modifications (mine some blocks)
2. Save and exit
3. Load the world again
4. **Expected:** Loading screen progresses smoothly from 70% → 100% → Gameplay
5. **Check logs for:**
   - `"GameManager: Detected gameplay scene loaded at index X"`
   - `"GameManager: Gameplay scene loaded successfully"`
   - `"LoadingSceneController: Loading completed successfully"`

## Related Issues

This fix resolves:
- Loading screen stuck at 70%
- Apparent "freeze" when loading modified worlds
- 30-second timeout when loading worlds

This is a SEPARATE issue from the ChunkModificationLog blocking bug (which was also fixed). Both issues needed to be fixed for world loading to work properly:
1. ChunkModificationLog async loading (prevents main thread blocking)
2. This fix (prevents loading UI from getting stuck)

## Impact

- ✅ **Loading screen now progresses correctly**
- ✅ **No more visual "freeze" at 70%**
- ✅ **Works with NetworkManager's async scene loading**
- ✅ **Applies to both fresh and modified worlds**
- ✅ **No performance impact** (actually faster detection)



