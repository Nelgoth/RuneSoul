// REPLACE ENTIRE FILE
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using System.Linq;

public static class TerrainAnalysisCache
{
    // Runtime cache
    private static readonly Dictionary<Vector3Int, TerrainAnalysisData> analysisCache = 
        new Dictionary<Vector3Int, TerrainAnalysisData>();
    
    // Persistent storage paths
    private static string cacheDirectory;
    private static readonly object cacheLock = new object();
    private static bool isDirty = false;
    private static float lastSaveTime = 0f;
    private static readonly float SAVE_INTERVAL = 30f;
    
    // Batch processing
    private static readonly HashSet<Vector3Int> pendingSaveCoords = new HashSet<Vector3Int>();
    private static readonly Queue<TerrainAnalysisData> saveBatchQueue = new Queue<TerrainAnalysisData>();
    private const int BATCH_SIZE = 250;
    private static readonly int MAX_CACHE_SIZE = 100000;
    private static int pruneCount = 0;
    
    // Background save task
    private static Task currentSaveTask = null;
    private static readonly object saveTaskLock = new object();
    private static bool isApplicationQuitting = false;
    
    // Track initialization state
    private static bool isInitialized = false;
    private static bool hasLoadedPersistentData = false;
    private static string worldId = null;
    private static string worldCacheFolder = null;

    private static HashSet<Vector3Int> recentlyAnalyzed = new HashSet<Vector3Int>();
    
    // Logging levels for debugging
    private enum LogLevel { None, Error, Warning, Info, Debug }
    private static LogLevel logLevel = LogLevel.Warning; 

    // Initialize on first use
    private static bool EnsureInitialized()
    {
        if (isInitialized)
            return true;

        try
        {
            cacheDirectory = Path.Combine(Application.persistentDataPath, "TerrainAnalysis");
            
            // Ensure directory exists
            if (!Directory.Exists(cacheDirectory))
            {
                Directory.CreateDirectory(cacheDirectory);
            }

            // Get world ID if available
            if (worldId == null && WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
            {
                worldId = WorldSaveManager.Instance.CurrentWorldId;
                LogMessage($"TerrainAnalysisCache initialized with WorldID: {worldId}", LogLevel.Info);
            }
            else if (worldId == null)
            {
                LogMessage("WorldSaveManager not available, using default cache folder", LogLevel.Warning);
            }

            isInitialized = true;
            return true;
        }
        catch (Exception e)
        {
            LogMessage($"Failed to initialize TerrainAnalysisCache: {e.Message}", LogLevel.Error);
            return false;
        }
    }

    private static void LogMessage(string message, LogLevel level)
    {
        if (level <= logLevel)
        {
            switch (level)
            {
                case LogLevel.Error:
                    Debug.LogError($"[TerrainCache] {message}");
                    break;
                case LogLevel.Warning:
                    Debug.LogWarning($"[TerrainCache] {message}");
                    break;
                case LogLevel.Info:
                case LogLevel.Debug:
                    Debug.Log($"[TerrainCache] {message}");
                    break;
            }
        }
    }

    private static int GetCacheSize() 
    {
        lock(cacheLock) 
        {
            return analysisCache.Count;
        }
    }

    private static string GetCacheFolder()
    {
        if (!EnsureInitialized())
            return null;
        
        // Return cached folder path if we have it
        if (!string.IsNullOrEmpty(worldCacheFolder) && Directory.Exists(worldCacheFolder))
            return worldCacheFolder;
        
        // Thread-safe check for WorldSaveManager - don't access it directly from background threads
        string currentWorldId = worldId;
        
        if (string.IsNullOrEmpty(currentWorldId))
        {
            // Get the worldId from the main thread safely
            if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
            {
                currentWorldId = WorldSaveManager.Instance.CurrentWorldId;
                worldId = currentWorldId; // Cache it for future use
                LogMessage($"Updated WorldID to: {worldId}", LogLevel.Debug);
            }
            else
            {
                // Use default directory if WorldSaveManager isn't available
                worldCacheFolder = Path.Combine(cacheDirectory, "Default");
                LogMessage($"Using default cache folder: {worldCacheFolder}", LogLevel.Warning);
                if (!Directory.Exists(worldCacheFolder))
                {
                    Directory.CreateDirectory(worldCacheFolder);
                }
                return worldCacheFolder;
            }
        }
        
        // Now construct the path using the current world ID
        string worldFolder = Path.Combine(Application.persistentDataPath, "Worlds", currentWorldId);
        string cacheFolder = Path.Combine(worldFolder, "TerrainCache");
        
        try {
            // Create directory if it doesn't exist
            if (!Directory.Exists(cacheFolder))
            {
                Directory.CreateDirectory(cacheFolder);
                LogMessage($"Created terrain cache folder: {cacheFolder}", LogLevel.Info);
            }
            
            // Cache the folder path for future use
            worldCacheFolder = cacheFolder;
            return cacheFolder;
        }
        catch (Exception e)
        {
            LogMessage($"Error creating terrain cache folder: {e.Message}", LogLevel.Error);
            return null;
        }
    }
    
    private static string GetCacheFilePath()
    {
        string cacheFolder = GetCacheFolder();
        if (string.IsNullOrEmpty(cacheFolder))
            return null;
            
        // Using .dat extension instead of .cache to avoid filtering issues
        return Path.Combine(cacheFolder, "TerrainAnalysis.dat");
    }

    private static bool LoadPersistentCache()
    {
        if (hasLoadedPersistentData)
            return true;
            
        try
        {
            string cacheFile = GetCacheFilePath();
            if (string.IsNullOrEmpty(cacheFile))
            {
                LogMessage("Cannot load terrain cache: Invalid cache file path", LogLevel.Error);
                return false;
            }
                
            if (!File.Exists(cacheFile))
            {
                LogMessage($"No existing terrain analysis cache found at: {cacheFile}", LogLevel.Info);
                hasLoadedPersistentData = true;
                return true;
            }

            LogMessage($"Loading terrain analysis cache from: {cacheFile}", LogLevel.Info);
            using (var fs = File.OpenRead(cacheFile))
            {
                BinaryFormatter formatter = new BinaryFormatter();
                var serializedData = (List<TerrainAnalysisData>)formatter.Deserialize(fs);
                
                // Validate data
                int validEntries = 0;
                int invalidEntries = 0;
                
                lock (cacheLock)
                {
                    analysisCache.Clear(); // Clear existing cache when loading new world
                    foreach (var data in serializedData)
                    {
                        if (ValidateAnalysisResult(data))
                        {
                            analysisCache[data.Coordinate.ToVector3Int()] = data;
                            validEntries++;
                        }
                        else
                        {
                            invalidEntries++;
                        }
                    }
                }
                
                hasLoadedPersistentData = true;
                LogMessage($"Loaded {validEntries} valid terrain analyses (rejected {invalidEntries} invalid entries)", LogLevel.Info);
                return true;
            }
        }
        catch (Exception e)
        {
            LogMessage($"Failed to load terrain analysis cache: {e.Message}\n{e.StackTrace}", LogLevel.Error);
            
            // Mark as loaded despite error to prevent continuous retries
            hasLoadedPersistentData = true;
            return false;
        }
    }

    public static void Update()
    {
        if (isApplicationQuitting)
            return;

        // Ensure we're initialized
        if (!EnsureInitialized())
            return;

        // Try to load cache if not already loaded
        if (!hasLoadedPersistentData)
        {
            // Update the worldId cache first
            if (WorldSaveManager.Instance != null)
            {
                worldId = WorldSaveManager.Instance.CurrentWorldId;
            }
            
            LoadPersistentCache();
        }

        // Process any pending saves in batches
        if (pendingSaveCoords.Count > 0)
        {
            ProcessPendingSaves();
        }

        // Check if it's time for a regular save
        if (Time.time - lastSaveTime > SAVE_INTERVAL && isDirty)
        {
            QueueFullSave();
            recentlyAnalyzed.Clear();
        }
    }

    public static void ResetCache()
    {
        if (!EnsureInitialized())
            return;
            
        lock (cacheLock)
        {
            analysisCache.Clear();
            pendingSaveCoords.Clear();
            saveBatchQueue.Clear();
            recentlyAnalyzed.Clear();
        }
        
        // Force reload of persistent data with new world ID
        hasLoadedPersistentData = false;
        
        // Update the worldId from the WorldSaveManager
        if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
        {
            worldId = WorldSaveManager.Instance.CurrentWorldId;
            worldCacheFolder = null; // Reset folder path cache
        }
        
        LogMessage("TerrainAnalysisCache reset for new world", LogLevel.Info);
    }

    public static void ForceSynchronize()
    {
        if (!EnsureInitialized())
            return;
            
        if (!hasLoadedPersistentData)
        {
            // Make sure we've loaded the latest data first
            LoadPersistentCache();
        }
        
        // Force a save of all cached data
        QueueFullSave();
        
        // Process pending saves immediately
        ProcessPendingSaves();
        
        // If there's an active save task, wait for it to complete
        if (currentSaveTask != null && !currentSaveTask.IsCompleted)
        {
            try
            {
                currentSaveTask.Wait();
            }
            catch (Exception e)
            {
                LogMessage($"Error waiting for terrain analysis save: {e.Message}", LogLevel.Error);
            }
        }
        
        LogMessage("TerrainAnalysisCache forced synchronization complete", LogLevel.Info);
    }

    private static void ProcessPendingSaves()
    {
        // Skip if a save is already in progress or we're quitting
        if ((currentSaveTask != null && !currentSaveTask.IsCompleted) || isApplicationQuitting)
        {
            return;
        }

        List<TerrainAnalysisData> batchData = null;
        
        lock (cacheLock)
        {
            // Fill batch queue if empty
            if (saveBatchQueue.Count == 0 && pendingSaveCoords.Count > 0)
            {
                var coordsToProcess = pendingSaveCoords.Take(BATCH_SIZE).ToList();
                foreach (var coord in coordsToProcess)
                {
                    if (analysisCache.TryGetValue(coord, out var data))
                    {
                        saveBatchQueue.Enqueue(data);
                    }
                    pendingSaveCoords.Remove(coord);
                }
            }

            // Process current batch
            if (saveBatchQueue.Count > 0)
            {
                batchData = new List<TerrainAnalysisData>(saveBatchQueue);
                saveBatchQueue.Clear();
            }
        }

        // Start the save task if we have data to save
        if (batchData != null && batchData.Count > 0)
        {
            currentSaveTask = MergeBatchAsync(batchData);
        }
    }

    private static async Task MergeBatchAsync(List<TerrainAnalysisData> batchData)
    {
        if (batchData == null || batchData.Count == 0 || isApplicationQuitting) 
            return;

        try
        {
            // Get the cache file path on the main thread before entering the task
            string cacheFilePath = GetCacheFilePath();
            if (string.IsNullOrEmpty(cacheFilePath))
            {
                LogMessage("Cannot save terrain analysis: Invalid cache file path", LogLevel.Error);
                return;
            }

            await Task.Run(() =>
            {
                try
                {
                    // Load existing data
                    List<TerrainAnalysisData> existingData = new List<TerrainAnalysisData>();
                    if (File.Exists(cacheFilePath))
                    {
                        using (var fs = File.OpenRead(cacheFilePath))
                        {
                            BinaryFormatter formatter = new BinaryFormatter();
                            existingData = (List<TerrainAnalysisData>)formatter.Deserialize(fs);
                        }
                    }

                    // Create a dictionary for faster lookup
                    Dictionary<SerializableVector3Int, TerrainAnalysisData> existingDict = 
                        existingData.ToDictionary(d => d.Coordinate, d => d);
                    
                    // Update existing entries or add new ones
                    foreach (var newData in batchData)
                    {
                        if (existingDict.TryGetValue(newData.Coordinate, out var existingEntry))
                        {
                            // Only replace if newer
                            if (newData.LastAnalyzed > existingEntry.LastAnalyzed)
                            {
                                existingDict[newData.Coordinate] = newData;
                            }
                        }
                        else
                        {
                            // Add new data
                            existingDict[newData.Coordinate] = newData;
                        }
                    }
                    
                    // Convert back to list
                    List<TerrainAnalysisData> mergedData = existingDict.Values.ToList();

                    // Save merged data
                    string tempPath = cacheFilePath + ".tmp";
                    lock (saveTaskLock)
                    {
                        // Ensure directory exists
                        string directory = Path.GetDirectoryName(tempPath);
                        if (!Directory.Exists(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }
                        
                        using (var fs = File.Create(tempPath))
                        {
                            BinaryFormatter formatter = new BinaryFormatter();
                            formatter.Serialize(fs, mergedData);
                        }

                        // Safe delete and move
                        if (File.Exists(cacheFilePath))
                        {
                            File.Delete(cacheFilePath);
                        }
                        File.Move(tempPath, cacheFilePath);
                    }

                    // Log on the main thread but don't use Unity API directly
                    System.Diagnostics.Debug.WriteLine($"Merged {batchData.Count} terrain analyses, total cached: {mergedData.Count}");
                }
                catch (Exception e)
                {
                    // Safe logging for background thread
                    System.Diagnostics.Debug.WriteLine($"Failed to merge terrain analysis batch: {e.Message}\n{e.StackTrace}");
                }
            });
            
            // Log from the main thread after the task completes
            LogMessage($"Successfully saved {batchData.Count} terrain analyses", LogLevel.Info);
        }
        catch (Exception e)
        {
            LogMessage($"Error scheduling terrain analysis batch: {e.Message}", LogLevel.Error);
        }
    }

    private static void QueueFullSave()
    {
        lock (cacheLock)
        {
            foreach (var coord in analysisCache.Keys)
            {
                if (!pendingSaveCoords.Contains(coord))
                {
                    pendingSaveCoords.Add(coord);
                }
            }
        }
        
        LogMessage($"Queued full save of {analysisCache.Count} terrain analyses", LogLevel.Info);
        isDirty = false;
        lastSaveTime = Time.time;
    }

    public static void InvalidateAnalysis(Vector3Int chunkCoord)
    {
        if (!EnsureInitialized())
            return;
            
        lock (cacheLock)
        {
            if (analysisCache.Remove(chunkCoord))
            {
                isDirty = true;
                pendingSaveCoords.Add(chunkCoord);
                LogMessage($"Invalidated analysis for chunk {chunkCoord}", LogLevel.Debug);
            }
        }
    }

    public static bool TryGetAnalysis(Vector3Int chunkCoord, out TerrainAnalysisData data)
    {
        data = null;
        
        if (!EnsureInitialized())
            return false;
            
        if (!hasLoadedPersistentData)
        {
            // Cache the world ID first
            if (WorldSaveManager.Instance != null)
            {
                worldId = WorldSaveManager.Instance.CurrentWorldId;
            }
            
            if (!LoadPersistentCache())
            {
                LogMessage($"Failed to load terrain analysis cache when looking up chunk {chunkCoord}", LogLevel.Warning);
                return false;
            }
        }

        lock (cacheLock)
        {
            return analysisCache.TryGetValue(chunkCoord, out data);
        }
    }

    private static void PruneCache()
    {
        // Only log at certain thresholds to reduce spam
        pruneCount++;
        bool shouldLog = pruneCount % 10 == 1; // Only log every 10th prune
        
        // Find the oldest non-empty entries to remove
        var oldestEntries = analysisCache
            .Where(kvp => !kvp.Value.IsEmpty) // Don't remove empty chunks first
            .OrderBy(kvp => kvp.Value.LastAnalyzed)
            .Take(MAX_CACHE_SIZE / 20) // Remove 5% of the cache (changed from 10%)
            .Select(kvp => kvp.Key)
            .ToList();
            
        foreach (var key in oldestEntries)
        {
            analysisCache.Remove(key);
        }
        
        if (shouldLog)
        {
            LogMessage($"Pruned {oldestEntries.Count} entries from terrain analysis cache (current size: {analysisCache.Count})", LogLevel.Info);
        }
    }

    public static bool IsChunkTrackedAsModified(Vector3Int chunkCoord)
    {
        if (!EnsureInitialized())
            return false;
                
        if (!hasLoadedPersistentData)
        {
            // Cache the world ID first
            if (WorldSaveManager.Instance != null)
            {
                worldId = WorldSaveManager.Instance.CurrentWorldId;
            }
                
            if (!LoadPersistentCache())
            {
                LogMessage($"Failed to load terrain analysis cache when checking modified status of chunk {chunkCoord}", LogLevel.Warning);
                return false;
            }
        }

        lock (cacheLock)
        {
            if (analysisCache.TryGetValue(chunkCoord, out var data))
            {
                return data.WasModified;
            }
        }
        
        return false;
    }

    public static void SaveAnalysis(Vector3Int coord, bool isEmpty, bool isSolid, bool wasModified = false)
    {
        if (!EnsureInitialized())
            return;
                
        // Skip if we've already analyzed this chunk recently
        if (recentlyAnalyzed.Contains(coord) && analysisCache.ContainsKey(coord) && !wasModified)
        {
            return;
        }

        // Validate that chunk isn't both empty and solid
        if (isEmpty && isSolid)
        {
            LogMessage($"Attempted to save invalid analysis for chunk {coord}: Cannot be both empty and solid", LogLevel.Error);
            return;
        }

        var data = new TerrainAnalysisData(coord, isEmpty, isSolid, wasModified);
        if (ValidateAnalysisResult(data))
        {
            lock (cacheLock)
            {
                analysisCache[coord] = data;
                recentlyAnalyzed.Add(coord);
                isDirty = true;
                
                // Force immediate save for modified solid chunks to prevent loss on crashes
                if (wasModified && (isEmpty || isSolid))
                {
                    pendingSaveCoords.Add(coord);
                }
                
                LogMessage($"Saved analysis for chunk {coord}: {(isEmpty ? "Empty" : isSolid ? "Solid" : "Mixed")}" +
                        (wasModified ? " (Modified)" : ""), LogLevel.Debug);
                
                // Only prune if we're significantly over the limit
                if (analysisCache.Count > MAX_CACHE_SIZE * 1.1f)
                {
                    PruneCache();
                }
            }
        }
    }

    private static bool ValidateAnalysisResult(TerrainAnalysisData data)
    {
        if (data == null)
        {
            LogMessage("Attempted to validate null terrain analysis data", LogLevel.Error);
            return false;
        }
        
        if (data.IsEmpty && data.IsSolid)
        {
            LogMessage($"Invalid analysis data for chunk {data.Coordinate}: Cannot be both empty and solid", LogLevel.Error);
            return false;
        }
        
        if (data.LastAnalyzed.Ticks == 0 || data.LastAnalyzed > DateTime.UtcNow.AddDays(1))
        {
            LogMessage($"Invalid timestamp in analysis data for chunk {data.Coordinate}: {data.LastAnalyzed}", LogLevel.Warning);
            return false;
        }
        
        return true;
    }

    public static void CleanupOldAnalysis()
    {
        if (!EnsureInitialized() || World.Instance == null)
            return;
            
        var config = World.Instance.Config;
        var maxAge = TimeSpan.FromDays(config.AnalysisCacheExpirationDays);
        var now = DateTime.UtcNow;
        var keysToRemove = new List<Vector3Int>();

        lock (cacheLock)
        {
            foreach (var kvp in analysisCache)
            {
                if (config.PermanentlyStoreEmptyChunks && kvp.Value.IsEmpty)
                    continue;

                if (now - kvp.Value.LastAnalyzed > maxAge)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                analysisCache.Remove(key);
                pendingSaveCoords.Add(key);
            }
        }

        if (keysToRemove.Count > 0)
        {
            isDirty = true;
            LogMessage($"Cleaned up {keysToRemove.Count} old terrain analyses", LogLevel.Info);
        }
    }

    private static void SaveFinalSynchronous()
    {
        if (!isDirty && pendingSaveCoords.Count == 0)
            return;
            
        LogMessage("Performing final synchronous terrain analysis save...", LogLevel.Info);
        
        try 
        {
            // Get data to save - we'll do this synchronously since the app is shutting down
            List<TerrainAnalysisData> allData;
            lock (cacheLock)
            {
                allData = new List<TerrainAnalysisData>(analysisCache.Values);
            }

            // Get cache file path
            string cachePath = GetCacheFilePath();
            if (string.IsNullOrEmpty(cachePath))
            {
                LogMessage("Cannot save terrain analysis: Invalid cache file path", LogLevel.Error);
                return;
            }
            
            // Load existing data
            List<TerrainAnalysisData> existingData = new List<TerrainAnalysisData>();
            if (File.Exists(cachePath))
            {
                using (var fs = File.OpenRead(cachePath))
                {
                    BinaryFormatter formatter = new BinaryFormatter();
                    existingData = (List<TerrainAnalysisData>)formatter.Deserialize(fs);
                }
            }

            // Create a dictionary for faster lookup
            Dictionary<SerializableVector3Int, TerrainAnalysisData> dataMap = 
                existingData.ToDictionary(d => d.Coordinate, d => d);
                
            // Update existing entries or add new ones
            foreach (var newData in allData)
            {
                if (dataMap.TryGetValue(newData.Coordinate, out var existingEntry))
                {
                    // Only replace if newer
                    if (newData.LastAnalyzed > existingEntry.LastAnalyzed)
                    {
                        dataMap[newData.Coordinate] = newData;
                    }
                }
                else
                {
                    // Add new data
                    dataMap[newData.Coordinate] = newData;
                }
            }
                
            // Convert back to list
            List<TerrainAnalysisData> finalData = dataMap.Values.ToList();

            // Ensure the directory exists
            string directory = Path.GetDirectoryName(cachePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            // Save directly - no temp file during shutdown for simplicity
            using (var fs = File.Create(cachePath))
            {
                BinaryFormatter formatter = new BinaryFormatter();
                formatter.Serialize(fs, finalData);
            }

            LogMessage($"Final terrain analysis save completed: {finalData.Count} total cached analyses", LogLevel.Info);
        }
        catch (Exception e)
        {
            LogMessage($"Final terrain analysis save failed: {e.Message}\n{e.StackTrace}", LogLevel.Error);
        }
    }

    public static void OnApplicationQuit()
    {
        LogMessage("TerrainAnalysisCache handling application quit...", LogLevel.Info);
        isApplicationQuitting = true;
        
        // Cache the world ID if it's not set yet
        if (string.IsNullOrEmpty(worldId) && WorldSaveManager.Instance != null)
        {
            worldId = WorldSaveManager.Instance.CurrentWorldId;
        }
        
        // Cancel any pending async tasks
        currentSaveTask = null;
        
        // Perform final synchronous save
        SaveFinalSynchronous();
    }

    [Serializable]
    public struct SerializableVector3Int
    {
        public int x;
        public int y;
        public int z;

        public SerializableVector3Int(Vector3Int v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
        }

        public Vector3Int ToVector3Int()
        {
            return new Vector3Int(x, y, z);
        }
        
        public override bool Equals(object obj)
        {
            if (obj is SerializableVector3Int other)
            {
                return x == other.x && y == other.y && z == other.z;
            }
            return false;
        }
        
        public override int GetHashCode()
        {
            return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
        }
    }
}