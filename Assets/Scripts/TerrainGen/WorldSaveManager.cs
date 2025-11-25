using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Manages saving and loading of world data, including terrain and player information
/// Acts as the central coordinator for all save/load operations
/// </summary>
public class WorldSaveManager : MonoBehaviour
{
    private static WorldSaveManager instance;
    public static WorldSaveManager Instance
    {
        get
        {
            if (instance == null)
            {
                // Find existing instance
                instance = FindAnyObjectByType<WorldSaveManager>();
                
                if (instance == null)
                {
                    Debug.LogWarning("No WorldSaveManager instance found in scene. Creating one dynamically.");

                    var managerObj = new GameObject("WorldSaveManager");
                    instance = managerObj.AddComponent<WorldSaveManager>();
                }
            }
            return instance;
        }
    }

    [Header("World Save Settings")]
    [SerializeField] private bool autoSaveEnabled = true;
    [SerializeField] private float autoSaveInterval = 300f; // 5 minutes
    
    private string currentWorldId;
    private string worldSaveFolder;
    private string worldMetadataPath;
    private float lastAutoSaveTime;
    private bool isInitialized = false;
    private WorldMetadata currentWorldMetadata;

    // Public properties
    public string CurrentWorldId => currentWorldId;
    public string WorldSaveFolder => worldSaveFolder;
    public bool IsInitialized => isInitialized;
    public WorldMetadata CurrentWorldMetadata => currentWorldMetadata;

    public WorldMetadata GetCurrentWorldMetadata(bool forceReload = false)
    {
        if (forceReload || currentWorldMetadata == null)
        {
            if (string.IsNullOrEmpty(currentWorldId))
            {
                Debug.LogWarning("[WorldSaveManager] Cannot reload metadata - no world ID set");
                return currentWorldMetadata;
            }

            currentWorldMetadata = LoadWorldMetadata(currentWorldId);
        }

        return currentWorldMetadata;
    }

    void Awake()
    {
        Debug.Log("[WorldSaveManager] Awake called");
        if (instance == null)
        {
            instance = this;
            
            // Make absolutely sure we're at root level
            if (transform.parent != null)
            {
                transform.parent = null;
            }
            
            // Explicitly mark for DontDestroyOnLoad
            DontDestroyOnLoad(gameObject);
            Debug.Log("[WorldSaveManager] Set as persistent using DontDestroyOnLoad");
            
            // Initialize default world
            InitializeDefaultWorld();
        }
        else if (instance != this)
        {
            Debug.Log("[WorldSaveManager] Destroying duplicate instance");
            Destroy(gameObject);
        }
    }

    private void InitializeDefaultWorld()
    {
        try
        {
            // Create base directory
            string worldsBaseDir = Path.Combine(Application.persistentDataPath, "Worlds");
            if (!Directory.Exists(worldsBaseDir))
            {
                Directory.CreateDirectory(worldsBaseDir);
            }

            // Create or use default world
            currentWorldId = "default_world";
            worldSaveFolder = Path.Combine(worldsBaseDir, currentWorldId);
            worldMetadataPath = Path.Combine(worldSaveFolder, "world.meta");
            WorldMetadata metadata = null;
            
            if (!Directory.Exists(worldSaveFolder))
            {
                Directory.CreateDirectory(worldSaveFolder);
                
                metadata = new WorldMetadata
                {
                    WorldId = currentWorldId,
                    WorldName = "Default World",
                    IsMultiplayerWorld = false,
                    CreatedDate = DateTime.UtcNow,
                    LastPlayed = DateTime.UtcNow,
                    WorldSeed = UnityEngine.Random.Range(1, 99999)
                };

                SaveWorldMetadata(metadata);
            }
            else
            {
                metadata = LoadWorldMetadata(currentWorldId);
            }

            if (metadata == null)
            {
                metadata = new WorldMetadata
                {
                    WorldId = currentWorldId,
                    WorldName = "Default World",
                    IsMultiplayerWorld = false,
                    CreatedDate = DateTime.UtcNow,
                    LastPlayed = DateTime.UtcNow,
                    WorldSeed = UnityEngine.Random.Range(1, 99999)
                };
                SaveWorldMetadata(metadata);
            }

            currentWorldMetadata = metadata;

            isInitialized = true;
            TerrainAnalysisCache.ResetCache();
            if (World.Instance != null)
             {
                 World.Instance.ResetTerrainAnalysisCache();
            }
            Debug.Log($"[WorldSaveManager] Initialized with default world at {worldSaveFolder}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[WorldSaveManager] Failed to initialize default world: {e.Message}\n{e.StackTrace}");
            throw;
        }
    }

    private void Update()
    {
        // Handle auto-save if enabled
        if (autoSaveEnabled && isInitialized && Time.time - lastAutoSaveTime > autoSaveInterval)
        {
            SaveWorld();
            lastAutoSaveTime = Time.time;
        }
    }

    public void InitializeWorld(string worldName = null, bool isMultiplayer = false, int? worldSeedOverride = null)
    {
        Debug.Log($"[WorldSaveManager] Initializing new world. Name: {worldName}, Multiplayer: {isMultiplayer}");
        
        try
        {
            // Create base directories
            string worldsBaseDir = Path.Combine(Application.persistentDataPath, "Worlds");
            if (!Directory.Exists(worldsBaseDir))
            {
                Debug.Log($"[WorldSaveManager] Creating worlds base directory: {worldsBaseDir}");
                Directory.CreateDirectory(worldsBaseDir);
            }

            // Generate new GUID for world ID
            currentWorldId = System.Guid.NewGuid().ToString();
            worldSaveFolder = Path.Combine(worldsBaseDir, currentWorldId);
            worldMetadataPath = Path.Combine(worldSaveFolder, "world.meta");

            // Ensure the world directory exists
            Debug.Log($"[WorldSaveManager] Creating world directory: {worldSaveFolder}");
            Directory.CreateDirectory(worldSaveFolder);

            // Create world seed if needed
            int worldSeed = worldSeedOverride ?? UnityEngine.Random.Range(1, 99999);

            // Create metadata
            var metadata = new WorldMetadata
            {
                WorldId = currentWorldId,
                WorldName = worldName ?? $"World_{currentWorldId.Substring(0, 8)}",
                IsMultiplayerWorld = isMultiplayer,
                CreatedDate = DateTime.UtcNow,
                LastPlayed = DateTime.UtcNow,
                WorldSeed = worldSeed
            };

            // Save metadata
            SaveWorldMetadata(metadata);
            currentWorldMetadata = metadata;
            
            // Set initialization flag
            isInitialized = true;
            TerrainAnalysisCache.ResetCache();
            
            Debug.Log($"[WorldSaveManager] World initialized successfully. ID: {currentWorldId}, Name: {metadata.WorldName}, Seed: {worldSeed}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[WorldSaveManager] Failed to initialize world: {e.Message}\n{e.StackTrace}");
            throw; // Re-throw to allow caller to handle the error
        }
    }

    public void InitializeClientWorld(string worldName, bool isMultiplayer)
    {
        Debug.Log($"[WorldSaveManager] Initializing client world. Name: {worldName}, Multiplayer: {isMultiplayer}");
        
        if (string.IsNullOrEmpty(worldName))
        {
            worldName = $"Client_World_{System.DateTime.Now:yyyyMMdd_HHmmss}";
        }

        // Generate a unique ID for the client world
        currentWorldId = System.Guid.NewGuid().ToString();
        
        // Create the world directory
        string worldsBaseDir = Path.Combine(Application.persistentDataPath, "Worlds");
        worldSaveFolder = Path.Combine(worldsBaseDir, currentWorldId);
        worldMetadataPath = Path.Combine(worldSaveFolder, "world.meta");
        
        if (!Directory.Exists(worldSaveFolder))
        {
            Directory.CreateDirectory(worldSaveFolder);
            Debug.Log($"[WorldSaveManager] Created client world directory: {worldSaveFolder}");
        }

        var metadata = new WorldMetadata
        {
            WorldId = currentWorldId,
            WorldName = worldName,
            IsMultiplayerWorld = isMultiplayer,
            CreatedDate = DateTime.UtcNow,
            LastPlayed = DateTime.UtcNow,
            WorldSeed = 0
        };

        SaveWorldMetadata(metadata);
        currentWorldMetadata = metadata;
        Debug.Log($"[WorldSaveManager] Client world initialized. ID: {currentWorldId}");
        
        // Reset the auto-save timer
        lastAutoSaveTime = Time.time;
        isInitialized = true;
        TerrainAnalysisCache.ResetCache();
    }

    public bool LoadWorld(string worldId)
    {
        Debug.Log($"[WorldSaveManager] Attempting to load world: {worldId}");
        
        // Ensure base Worlds directory exists
        string worldsBaseDir = Path.Combine(Application.persistentDataPath, "Worlds");
        if (!Directory.Exists(worldsBaseDir))
        {
            Debug.LogWarning($"[WorldSaveManager] Base worlds directory not found, creating: {worldsBaseDir}");
            Directory.CreateDirectory(worldsBaseDir);
        }

        string targetFolder = Path.Combine(worldsBaseDir, worldId);
        if (!Directory.Exists(targetFolder))
        {
            Debug.LogWarning($"[WorldSaveManager] World directory not found: {targetFolder}");
            return false;
        }

        // Check if we're changing worlds BEFORE updating the fields
        bool isChangingWorlds = (currentWorldId != worldId);

        // UPDATE FIELDS FIRST before resetting caches
        // This is critical - SaveSystem.ResetPathCache() needs the NEW path
        currentWorldId = worldId;
        worldSaveFolder = targetFolder;
        worldMetadataPath = Path.Combine(worldSaveFolder, "world.meta");

        // Reset caches when changing worlds (now uses the updated worldSaveFolder)
        if (isChangingWorlds)
        {
            Debug.Log($"[WorldSaveManager] Resetting SaveSystem caches for new world: {worldId}");
            SaveSystem.ResetPathCache();
        }

        var metadata = LoadWorldMetadata(worldId);
        if (metadata != null)
        {
            metadata.LastPlayed = DateTime.UtcNow;
            SaveWorldMetadata(metadata);
            currentWorldMetadata = metadata;
            
            lastAutoSaveTime = Time.time;
            isInitialized = true;
            TerrainAnalysisCache.ResetCache();
            Debug.Log($"[WorldSaveManager] World loaded successfully: {metadata.WorldName}");
            
            return true;
        }

        Debug.LogError("[WorldSaveManager] Failed to load world metadata");
        return false;
    }

    private void SaveWorldMetadata(WorldMetadata metadata)
    {
        try
        {
            string json = JsonUtility.ToJson(metadata, true);
            File.WriteAllText(worldMetadataPath, json);
            Debug.Log($"[WorldSaveManager] Metadata saved to: {worldMetadataPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[WorldSaveManager] Failed to save metadata: {e.Message}");
        }
    }

    private WorldMetadata LoadWorldMetadata(string worldId)
    {
        string path = Path.Combine(Application.persistentDataPath, "Worlds", worldId, "world.meta");
        if (!File.Exists(path))
        {
            Debug.LogError($"[WorldSaveManager] Metadata file not found: {path}");
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonUtility.FromJson<WorldMetadata>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[WorldSaveManager] Failed to load metadata: {e.Message}");
            return null;
        }
    }

    public WorldMetadata[] GetAvailableWorlds()
    {
        Debug.Log("[WorldSaveManager] Getting available worlds");
        string worldsFolder = Path.Combine(Application.persistentDataPath, "Worlds");
        
        if (!Directory.Exists(worldsFolder))
        {
            Debug.Log($"[WorldSaveManager] Creating worlds directory at: {worldsFolder}");
            Directory.CreateDirectory(worldsFolder);
            return new WorldMetadata[0];
        }

        List<WorldMetadata> worlds = new List<WorldMetadata>();
        var directories = Directory.GetDirectories(worldsFolder);
        Debug.Log($"[WorldSaveManager] Found {directories.Length} world directories");

        foreach (string dir in directories)
        {
            try
            {
                string worldId = Path.GetFileName(dir);
                string metaPath = Path.Combine(dir, "world.meta");
                
                if (File.Exists(metaPath))
                {
                    Debug.Log($"[WorldSaveManager] Loading metadata for world: {worldId}");
                    var metadata = LoadWorldMetadata(worldId);
                    if (metadata != null)
                    {
                        worlds.Add(metadata);
                        Debug.Log($"[WorldSaveManager] Loaded world: {metadata.WorldName}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[WorldSaveManager] No metadata file found for world: {worldId}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[WorldSaveManager] Error loading world in directory {dir}: {e.Message}");
            }
        }

        Debug.Log($"[WorldSaveManager] Successfully loaded {worlds.Count} worlds");
        return worlds.ToArray();
    }

    public void SaveWorld()
    {
        if (!isInitialized)
        {
            Debug.LogError("[WorldSaveManager] Cannot save world - not initialized");
            return;
        }

        Debug.Log("[WorldSaveManager] Saving world...");

        try
        {
            // Make sure we have up-to-date metadata
            var metadata = LoadWorldMetadata(currentWorldId);
            if (metadata != null)
            {
                metadata.LastPlayed = DateTime.UtcNow;
                SaveWorldMetadata(metadata);
                currentWorldMetadata = metadata;
            }

            // Save all player positions if we're on the server
            if (Unity.Netcode.NetworkManager.Singleton != null && 
                Unity.Netcode.NetworkManager.Singleton.IsServer &&
                PlayerSpawner.Instance != null)
            {
                // Get all spawned player objects
                foreach (var netObj in Unity.Netcode.NetworkManager.Singleton.SpawnManager.SpawnedObjects.Values)
                {
                    if (netObj.IsPlayerObject)
                    {
                        ulong clientId = netObj.OwnerClientId;
                        Vector3 position = netObj.transform.position;
                        PlayerSpawner.Instance.SavePlayerPosition(clientId, position);
                        Debug.Log($"[WorldSaveManager] Saved position for player {clientId} during world save");
                    }
                }
            }

            // Force save terrain analysis cache
            TerrainAnalysisCache.Update();

            // Trigger save in World instance for all chunks
            if (World.Instance != null)
            {
                World.Instance.PrepareForShutdown();
            }

            Debug.Log("[WorldSaveManager] World saved successfully");
        }
        catch (Exception e)
        {
            Debug.LogError($"[WorldSaveManager] Failed to save world: {e.Message}\n{e.StackTrace}");
        }
    }

    public void SavePlayerData(ulong clientId, string playerName, Vector3 position, Quaternion rotation)
    {
        if (!isInitialized)
        {
            Debug.LogError("[WorldSaveManager] Cannot save player data - not initialized");
            return;
        }

        try
        {
            // Create player data directory if it doesn't exist
            string playerDataDir = Path.Combine(worldSaveFolder, "Players");
            if (!Directory.Exists(playerDataDir))
            {
                Directory.CreateDirectory(playerDataDir);
            }

            // Create player data
            var playerData = new PlayerSaveData
            {
                ClientId = clientId.ToString(),
                PlayerName = playerName,
                PositionX = position.x,
                PositionY = position.y,
                PositionZ = position.z,
                RotationX = rotation.x,
                RotationY = rotation.y,
                RotationZ = rotation.z,
                RotationW = rotation.w,
                LastSaved = DateTime.UtcNow
            };

            // Save player data to disk
            string playerDataPath = Path.Combine(playerDataDir, $"player_{clientId}.json");
            string json = JsonUtility.ToJson(playerData, true);
            File.WriteAllText(playerDataPath, json);

            Debug.Log($"[WorldSaveManager] Saved data for player {clientId} at {playerDataPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[WorldSaveManager] Failed to save player data: {e.Message}");
        }
    }

    public PlayerSaveData LoadPlayerData(ulong clientId)
    {
        Debug.Log($"[WorldSaveManager] LoadPlayerData called for client {clientId}");
        
        if (!isInitialized)
        {
            Debug.LogError($"[WorldSaveManager] Cannot load player data - not initialized! worldSaveFolder={worldSaveFolder}");
            return null;
        }

        try
        {
            string playerDataPath = Path.Combine(worldSaveFolder, "Players", $"player_{clientId}.json");
            Debug.Log($"[WorldSaveManager] Looking for player data at: {playerDataPath}");
            
            if (!File.Exists(playerDataPath))
            {
                Debug.LogWarning($"[WorldSaveManager] ❌ No saved data file found for player {clientId} at {playerDataPath}");
                return null;
            }

            string json = File.ReadAllText(playerDataPath);
            Debug.Log($"[WorldSaveManager] Read JSON data: {json}");
            
            var playerData = JsonUtility.FromJson<PlayerSaveData>(json);

            if (playerData != null)
            {
                Vector3 loadedPosition = playerData.Position;
                Debug.Log($"[WorldSaveManager] ✅ Successfully loaded data for player {clientId}. Position: {loadedPosition}");
            }
            else
            {
                Debug.LogError($"[WorldSaveManager] Failed to deserialize player data from JSON");
            }

            return playerData;
        }
        catch (Exception e)
        {
            Debug.LogError($"[WorldSaveManager] Failed to load player data: {e.Message}\n{e.StackTrace}");
            return null;
        }
    }

    private void OnApplicationQuit()
    {
        // Save world on application quit
        if (isInitialized)
        {
            SaveWorld();
        }
    }

    public string GetWorldSubdirectory(string subDirName)
    {
        if (!isInitialized)
        {
            Debug.LogError("[WorldSaveManager] Cannot get subdirectory - not initialized");
            return null;
        }

        string path = Path.Combine(worldSaveFolder, subDirName);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }
}

[System.Serializable]
public class PlayerSaveData
{
    public string ClientId;
    public string PlayerName;
    public float PositionX;
    public float PositionY;
    public float PositionZ;
    public float RotationX;
    public float RotationY;
    public float RotationZ;
    public float RotationW;
    public DateTime LastSaved;

    // Helper methods to convert to/from Unity types
    public Vector3 Position => new Vector3(PositionX, PositionY, PositionZ);
    public Quaternion Rotation => new Quaternion(RotationX, RotationY, RotationZ, RotationW);
}