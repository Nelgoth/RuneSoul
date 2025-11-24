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
    private static readonly HashSet<Vector3Int> pendingDeleteCoords = new HashSet<Vector3Int>();
    private static readonly Queue<Vector3Int> deleteBatchQueue = new Queue<Vector3Int>();
    private const int BATCH_SIZE = 250;
    private const int ACCELERATED_BATCH_SIZE = 8192;
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
    private static string cachedFolderWorldId = null;
    private const string DEFAULT_WORLD_ID = "__default";

    private static HashSet<Vector3Int> recentlyAnalyzed = new HashSet<Vector3Int>();
    
    private static bool synchronousFlushMode = false;
    
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
            // Set base path for reference, but don't create it
            // The actual cache folders are created in GetCacheFolder() as needed
            cacheDirectory = Path.Combine(Application.persistentDataPath, "TerrainAnalysis");

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

    public static void SetLogVerbosity(TerrainConfigs.LogVerbosity verbosity)
    {
        switch (verbosity)
        {
            case TerrainConfigs.LogVerbosity.Debug:
                logLevel = LogLevel.Debug;
                break;
            case TerrainConfigs.LogVerbosity.Info:
                logLevel = LogLevel.Info;
                break;
            case TerrainConfigs.LogVerbosity.Warnings:
                logLevel = LogLevel.Warning;
                break;
            case TerrainConfigs.LogVerbosity.ErrorsOnly:
            default:
                logLevel = LogLevel.Error;
                break;
        }
    }

    public static void ApplyLoggingFromConfig(TerrainConfigs configs)
    {
        if (configs == null)
            return;

        SetLogVerbosity(configs.terrainCacheLogLevel);
    }

    private static int GetCacheSize() 
    {
        lock(cacheLock) 
        {
            return analysisCache.Count;
        }
    }

    public static void SetSynchronousFlushMode(bool enabled)
    {
        if (synchronousFlushMode == enabled)
            return;

        synchronousFlushMode = enabled;

        if (enabled)
        {
            ProcessPendingSavesInternal(true, ACCELERATED_BATCH_SIZE);
        }
    }

    public static bool IsSynchronousFlushMode => synchronousFlushMode;

    public static int GetPendingSaveCount()
    {
        lock (cacheLock)
        {
            return pendingSaveCoords.Count + saveBatchQueue.Count +
                   pendingDeleteCoords.Count + deleteBatchQueue.Count;
        }
    }

    public static bool HasPendingWork()
    {
        lock (cacheLock)
        {
            return pendingSaveCoords.Count > 0 || saveBatchQueue.Count > 0 ||
                   pendingDeleteCoords.Count > 0 || deleteBatchQueue.Count > 0 ||
                   currentSaveTask != null;
        }
    }

    public static int ProcessPendingSavesImmediate(int batchOverride = -1)
    {
        return ProcessPendingSavesInternal(false, batchOverride);  // Changed to FALSE - force synchronous during immediate processing
    }

    private static string GetCacheFolder()
    {
        if (!EnsureInitialized())
            return null;
        
        // Thread-safe check for WorldSaveManager - don't access it directly from background threads
        string currentWorldId = worldId;
        if (string.IsNullOrEmpty(currentWorldId) && WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
        {
            currentWorldId = WorldSaveManager.Instance.CurrentWorldId;
            worldId = currentWorldId;
            LogMessage($"Updated WorldID to: {worldId}", LogLevel.Debug);
        }

        string targetWorldKey = string.IsNullOrEmpty(currentWorldId) ? DEFAULT_WORLD_ID : currentWorldId;

        if (!string.IsNullOrEmpty(worldCacheFolder) &&
            string.Equals(cachedFolderWorldId, targetWorldKey, StringComparison.Ordinal) &&
            Directory.Exists(worldCacheFolder))
        {
            return worldCacheFolder;
        }

        string folderPath;
        if (targetWorldKey == DEFAULT_WORLD_ID)
        {
            folderPath = Path.Combine(cacheDirectory, "Default");
            if (!string.Equals(cachedFolderWorldId, targetWorldKey, StringComparison.Ordinal))
            {
                LogMessage($"Using default cache folder: {folderPath}", LogLevel.Warning);
            }
        }
        else
        {
            string worldFolder = Path.Combine(Application.persistentDataPath, "Worlds", targetWorldKey);
            folderPath = Path.Combine(worldFolder, "TerrainCache");
        }
        
        try
        {
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
                LogMessage($"Created terrain cache folder: {folderPath}", LogLevel.Info);
            }
            
            worldCacheFolder = folderPath;
            cachedFolderWorldId = targetWorldKey;
            return worldCacheFolder;
        }
        catch (Exception e)
        {
            LogMessage($"Error creating terrain cache folder: {e.Message}", LogLevel.Error);
            return null;
        }
    }
    
    public static string GetCacheFilePath()
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
            Debug.Log("[TerrainAnalysisCache] LoadPersistentCache() called");
            
            string cacheFile = GetCacheFilePath();
            if (string.IsNullOrEmpty(cacheFile))
            {
                LogMessage("Cannot load terrain cache: Invalid cache file path", LogLevel.Error);
                hasLoadedPersistentData = true; // Mark as loaded to prevent retries
                return false;
            }
                
            if (!File.Exists(cacheFile))
            {
                LogMessage($"No existing terrain analysis cache found at: {cacheFile}", LogLevel.Info);
                hasLoadedPersistentData = true;
                return true;
            }

            Debug.Log($"[TerrainAnalysisCache] Found cache file, size: {new FileInfo(cacheFile).Length} bytes");
            Debug.Log($"[TerrainAnalysisCache] Found cache file, size: {new FileInfo(cacheFile).Length} bytes");
            LogMessage($"Loading terrain analysis cache from: {cacheFile}", LogLevel.Info);
            
            Debug.Log("[TerrainAnalysisCache] Opening file stream...");
            using (var fs = File.OpenRead(cacheFile))
            {
                Debug.Log("[TerrainAnalysisCache] Deserializing data...");
                BinaryFormatter formatter = new BinaryFormatter();
                var serializedData = (List<TerrainAnalysisData>)formatter.Deserialize(fs);
                
                Debug.Log($"[TerrainAnalysisCache] Deserialized {serializedData?.Count ?? 0} entries");
                
                // Validate data
                int validEntries = 0;
                int invalidEntries = 0;
                
                Debug.Log("[TerrainAnalysisCache] Acquiring cache lock...");
                lock (cacheLock)
                {
                    Debug.Log("[TerrainAnalysisCache] Lock acquired, clearing caches...");
                    analysisCache.Clear(); // Clear existing cache when loading new world
                    recentlyAnalyzed.Clear(); // Clear this too so we can repopulate it
                    
                    Debug.Log("[TerrainAnalysisCache] Processing entries...");
                    foreach (var data in serializedData)
                    {
                        if (ValidateAnalysisResult(data))
                        {
                            Vector3Int coord = data.Coordinate.ToVector3Int();
                            analysisCache[coord] = data;
                            recentlyAnalyzed.Add(coord); // CRITICAL: Mark as recently analyzed to prevent re-queueing
                            validEntries++;
                        }
                        else
                        {
                            invalidEntries++;
                        }
                    }
                    Debug.Log($"[TerrainAnalysisCache] Processing complete. Valid: {validEntries}, Invalid: {invalidEntries}");
                }
                
                hasLoadedPersistentData = true;
                Debug.Log("[TerrainAnalysisCache] Cache load completed successfully");
                LogMessage($"Loaded {validEntries} valid terrain analyses (rejected {invalidEntries} invalid entries)", LogLevel.Info);
                return true;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[TerrainAnalysisCache] CRITICAL ERROR during cache load: {e.GetType().Name}: {e.Message}");
            Debug.LogError($"[TerrainAnalysisCache] Stack trace: {e.StackTrace}");
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

        // CRITICAL: Don't try to load cache during early scene initialization
        // World must be fully initialized before we attempt to load cache files
        if (World.Instance == null || !World.Instance.IsWorldFullyInitialized)
        {
            return; // Wait for world to be ready
        }

        // Try to load cache if not already loaded
        if (!hasLoadedPersistentData)
        {
            // Update the worldId cache first
            if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
            {
                worldId = WorldSaveManager.Instance.CurrentWorldId;
                
                // Only try to load if we have a valid world ID
                if (!string.IsNullOrEmpty(worldId))
                {
                    LoadPersistentCache();
                }
            }
        }

        // Process any pending saves in batches
        if (pendingSaveCoords.Count > 0)
        {
            // Log if we have a suspiciously large number of pending saves (but only periodically to avoid spam)
            if (pendingSaveCoords.Count > 1000 && Time.frameCount % 60 == 0)
            {
                LogMessage($"Large terrain cache save queue: {pendingSaveCoords.Count} chunks pending (recentlyAnalyzed has {recentlyAnalyzed.Count} entries)", LogLevel.Warning);
            }
            
            int batchOverride = synchronousFlushMode ? Mathf.Max(ACCELERATED_BATCH_SIZE, BATCH_SIZE) : -1;
            ProcessPendingSavesInternal(true, batchOverride);
        }

        // Check if it's time for a regular save
        // CRITICAL FIX: Don't call QueueFullSave() for periodic saves!
        // SaveAnalysis() already adds changed chunks to pendingSaveCoords
        // QueueFullSave() was queueing ALL chunks (potentially 10K-100K) every 30 seconds
        if (!synchronousFlushMode && Time.time - lastSaveTime > SAVE_INTERVAL && isDirty)
        {
            // Just log and reset the timer - chunks are already queued via SaveAnalysis()
            if (pendingSaveCoords.Count > 0)
            {
                LogMessage($"Periodic save: {pendingSaveCoords.Count} chunks pending", LogLevel.Info);
            }
            
            isDirty = false;
            lastSaveTime = Time.time;
            // DON'T clear recentlyAnalyzed - it prevents redundant saves
            // Only clear it when we actually need to (world change or reset)
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
            pendingDeleteCoords.Clear();
            deleteBatchQueue.Clear();
            recentlyAnalyzed.Clear(); // Only clear here when resetting cache
        }
        
        // Force reload of persistent data with new world ID
        hasLoadedPersistentData = false;
        
        // Update the worldId from the WorldSaveManager
        if (WorldSaveManager.Instance != null && WorldSaveManager.Instance.IsInitialized)
        {
            worldId = WorldSaveManager.Instance.CurrentWorldId;
            worldCacheFolder = null; // Reset folder path cache
            cachedFolderWorldId = null;
        }
        else
        {
            worldCacheFolder = null;
            cachedFolderWorldId = null;
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
        
        while (HasPendingWork())
        {
            ProcessPendingSavesInternal(false, int.MaxValue);
        }
        
        LogMessage("TerrainAnalysisCache forced synchronization complete", LogLevel.Info);
    }

    private static int ProcessPendingSavesInternal(bool allowAsync, int batchOverride = -1)
    {
        if (isApplicationQuitting)
        {
            return 0;
        }

        try
        {
            if (currentSaveTask != null)
            {
                if (allowAsync)
                {
                    if (!currentSaveTask.IsCompleted)
                    {
                        LogMessage($"Save task still in progress (async mode), skipping batch. PendingSaves: {pendingSaveCoords.Count}", LogLevel.Debug);
                        return 0;
                    }
                }
                else
                {
                    try
                    {
                        Debug.Log($"[TerrainAnalysisCache] Waiting for save task to complete (synchronous mode)...");
                        // Add timeout to prevent hanging
                        if (!currentSaveTask.Wait(TimeSpan.FromSeconds(5)))
                        {
                            Debug.LogWarning("[TerrainAnalysisCache] Terrain analysis save task timed out after 5 seconds - FORCING CLEAR");
                            LogMessage("Terrain analysis save task timed out after 5 seconds - clearing task", LogLevel.Warning);
                            currentSaveTask = null; // Force clear to prevent infinite hang
                            return 0;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[TerrainAnalysisCache] Error waiting for save task: {e.Message}");
                        LogMessage($"Error completing terrain analysis save task: {e.Message}", LogLevel.Error);
                        currentSaveTask = null; // Clear task to prevent getting stuck
                    }
                }

                currentSaveTask = null;
            }

        List<TerrainAnalysisData> batchData = null;
        List<Vector3Int> deleteBatch = null;
        int processedCount = 0;

        lock (cacheLock)
        {
            if (saveBatchQueue.Count == 0 && pendingSaveCoords.Count > 0)
            {
                int targetBatchSize = batchOverride > 0
                    ? batchOverride
                    : (allowAsync ? BATCH_SIZE : Mathf.Max(BATCH_SIZE, pendingSaveCoords.Count));

                if (!allowAsync && pendingSaveCoords.Count > 1000)
                {
                    Debug.Log($"[TerrainAnalysisCache] Processing large batch: {pendingSaveCoords.Count} pending saves, batch size: {targetBatchSize}");
                }

                var coordsToProcess = pendingSaveCoords.Take(targetBatchSize).ToList();
                foreach (var coord in coordsToProcess)
                {
                    if (analysisCache.TryGetValue(coord, out var data))
                    {
                        saveBatchQueue.Enqueue(data);
                    }
                    pendingSaveCoords.Remove(coord);
                }
            }

            if (deleteBatchQueue.Count == 0 && pendingDeleteCoords.Count > 0)
            {
                int targetDeleteSize = batchOverride > 0
                    ? batchOverride
                    : (allowAsync ? BATCH_SIZE : Mathf.Max(BATCH_SIZE, pendingDeleteCoords.Count));

                var coordsToDelete = pendingDeleteCoords.Take(targetDeleteSize).ToList();
                foreach (var coord in coordsToDelete)
                {
                    deleteBatchQueue.Enqueue(coord);
                    pendingDeleteCoords.Remove(coord);
                }
            }

            if (saveBatchQueue.Count > 0)
            {
                batchData = new List<TerrainAnalysisData>(saveBatchQueue);
                processedCount = batchData.Count;
                saveBatchQueue.Clear();
            }
            if (deleteBatchQueue.Count > 0)
            {
                deleteBatch = new List<Vector3Int>(deleteBatchQueue);
                processedCount += deleteBatch.Count;
                deleteBatchQueue.Clear();
            }

            if (deleteBatchQueue.Count == 0 && pendingSaveCoords.Count == 0 && pendingDeleteCoords.Count == 0 && !allowAsync)
            {
                if (isDirty)
                {
                    isDirty = false;
                    lastSaveTime = Time.time;
                }
            }
        }

            bool hasWork = (batchData != null && batchData.Count > 0) ||
                           (deleteBatch != null && deleteBatch.Count > 0);

            if (hasWork)
            {
                if (allowAsync)
                {
                    currentSaveTask = MergeBatchAsync(
                        batchData != null ? new List<TerrainAnalysisData>(batchData) : null,
                        deleteBatch != null ? new List<Vector3Int>(deleteBatch) : null);
                    return 0;
                }

                MergeBatchData(batchData, deleteBatch);
                return processedCount;
            }

            return 0;
        }
        catch (Exception e)
        {
            LogMessage($"Critical error in ProcessPendingSavesInternal: {e.Message}\n{e.StackTrace}", LogLevel.Error);
            return 0;
        }
    }

    private static async Task MergeBatchAsync(List<TerrainAnalysisData> batchData, List<Vector3Int> deleteCoords)
    {
        if ((batchData == null || batchData.Count == 0) && (deleteCoords == null || deleteCoords.Count == 0))
            return;

        if (isApplicationQuitting)
            return;

        // IMPORTANT: Resolve the cache path on the main thread before jumping to a background task.
        string cacheFilePath = GetCacheFilePath();
        if (string.IsNullOrEmpty(cacheFilePath))
        {
            LogMessage("Cannot save terrain analysis: Invalid cache file path", LogLevel.Error);
            return;
        }

        try
        {
            await Task.Run(() => MergeBatchData(batchData, deleteCoords, cacheFilePath));
            int saveCount = batchData?.Count ?? 0;
            int deleteCount = deleteCoords?.Count ?? 0;
            LogMessage($"Persisted terrain cache batch (saved {saveCount}, deleted {deleteCount})", LogLevel.Info);
        }
        catch (Exception e)
        {
            LogMessage($"Error scheduling terrain analysis batch: {e.Message}", LogLevel.Error);
        }
    }

    private static void MergeBatchData(List<TerrainAnalysisData> batchData, List<Vector3Int> deleteCoords, string cacheFilePath = null)
    {
        if ((batchData == null || batchData.Count == 0) && (deleteCoords == null || deleteCoords.Count == 0))
            return;

        if (isApplicationQuitting)
            return;

        try
        {
            if (string.IsNullOrEmpty(cacheFilePath))
            {
                cacheFilePath = GetCacheFilePath();
                if (string.IsNullOrEmpty(cacheFilePath))
                {
                    LogMessage("Cannot save terrain analysis: Invalid cache file path", LogLevel.Error);
                    return;
                }
            }

            int saveCount = batchData?.Count ?? 0;
            int deleteCount = deleteCoords?.Count ?? 0;
            Debug.Log($"[TerrainAnalysisCache] MergeBatchData starting: {saveCount} saves, {deleteCount} deletes");
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

            List<TerrainAnalysisData> existingData = new List<TerrainAnalysisData>();
            if (File.Exists(cacheFilePath))
            {
                Debug.Log($"[TerrainAnalysisCache] Loading existing cache file: {cacheFilePath}");
                using (var fs = File.OpenRead(cacheFilePath))
                {
                    BinaryFormatter formatter = new BinaryFormatter();
                    existingData = (List<TerrainAnalysisData>)formatter.Deserialize(fs);
                }
                Debug.Log($"[TerrainAnalysisCache] Loaded {existingData.Count} existing entries in {sw.ElapsedMilliseconds}ms");
            }

            Dictionary<SerializableVector3Int, TerrainAnalysisData> existingDict =
                existingData.ToDictionary(d => d.Coordinate, d => d);

            if (deleteCoords != null && deleteCoords.Count > 0)
            {
                foreach (var coord in deleteCoords)
                {
                    existingDict.Remove(new SerializableVector3Int(coord));
                }
            }

            foreach (var newData in batchData ?? Enumerable.Empty<TerrainAnalysisData>())
            {
                if (existingDict.TryGetValue(newData.Coordinate, out var existingEntry))
                {
                    if (newData.LastAnalyzed > existingEntry.LastAnalyzed)
                    {
                        existingDict[newData.Coordinate] = newData;
                    }
                }
                else
                {
                    existingDict[newData.Coordinate] = newData;
                }
            }

            List<TerrainAnalysisData> mergedData = existingDict.Values.ToList();
            Debug.Log($"[TerrainAnalysisCache] Merged data, total entries: {mergedData.Count}");

            string tempPath = cacheFilePath + ".tmp";
            lock (saveTaskLock)
            {
                string directory = Path.GetDirectoryName(tempPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Debug.Log($"[TerrainAnalysisCache] Writing to temp file: {tempPath}");
                using (var fs = File.Create(tempPath))
                {
                    BinaryFormatter formatter = new BinaryFormatter();
                    formatter.Serialize(fs, mergedData);
                }

                Debug.Log($"[TerrainAnalysisCache] Moving temp file to cache file");
                if (File.Exists(cacheFilePath))
                {
                    File.Delete(cacheFilePath);
                }
                File.Move(tempPath, cacheFilePath);
                
                sw.Stop();
                Debug.Log($"[TerrainAnalysisCache] MergeBatchData completed in {sw.ElapsedMilliseconds}ms");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[TerrainAnalysisCache] ERROR in MergeBatchData: {e.Message}\n{e.StackTrace}");
            LogMessage($"Error saving terrain analysis batch: {e.Message}\n{e.StackTrace}", LogLevel.Error);
        }
    }

    private static void QueueFullSave()
    {
        // This method is only used for forced synchronization (ForceSynchronize, OnApplicationQuit)
        // NOT for periodic saves (which now just use pendingSaveCoords directly)
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
                pendingDeleteCoords.Add(chunkCoord);
                pendingSaveCoords.Remove(chunkCoord);
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
            pendingDeleteCoords.Add(key);
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

        // Validate that chunk isn't both empty and solid
        if (isEmpty && isSolid)
        {
            LogMessage($"Attempted to save invalid analysis for chunk {coord}: Cannot be both empty and solid", LogLevel.Error);
            return;
        }

        var data = new TerrainAnalysisData(coord, isEmpty, isSolid, wasModified);
        if (!ValidateAnalysisResult(data))
            return;

        try
        {
            lock (cacheLock)
        {
            // Check if entry exists
            if (analysisCache.TryGetValue(coord, out var existingData))
            {
                bool entryChanged = existingData.IsEmpty != isEmpty ||
                                    existingData.IsSolid != isSolid ||
                                    existingData.WasModified != wasModified;

                if (!entryChanged)
                {
                    // Entry unchanged - just update timestamp and skip save
                    existingData.LastAnalyzedTicks = DateTime.UtcNow.Ticks;
                    recentlyAnalyzed.Add(coord);
                    pendingDeleteCoords.Remove(coord);
                    return; // EARLY EXIT - don't add to pendingSaveCoords
                }
            }

            // Entry changed or is new - save it
            analysisCache[coord] = data;
            recentlyAnalyzed.Add(coord);
            isDirty = true;
            pendingDeleteCoords.Remove(coord);

            // Only schedule for persistence if data actually changed
            pendingSaveCoords.Add(coord);
            
            // Force immediate save for modified solid chunks to prevent loss on crashes
            if (wasModified && (isEmpty || isSolid))
            {
                LogMessage($"Queuing modified solid/empty chunk {coord} for immediate terrain analysis save", LogLevel.Info);
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
        catch (Exception e)
        {
            LogMessage($"Error in SaveAnalysis for chunk {coord}: {e.Message}\n{e.StackTrace}", LogLevel.Error);
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
                pendingDeleteCoords.Add(key);
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
            List<Vector3Int> deletesSnapshot;
            lock (cacheLock)
            {
                allData = new List<TerrainAnalysisData>(analysisCache.Values);
                deletesSnapshot = new List<Vector3Int>(pendingDeleteCoords);
                pendingDeleteCoords.Clear();
                deleteBatchQueue.Clear();
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

            var liveKeys = new HashSet<SerializableVector3Int>(allData.Select(d => d.Coordinate));
            var deleteKeys = new List<SerializableVector3Int>();
            foreach (var deleteCoord in deletesSnapshot)
            {
                var key = new SerializableVector3Int(deleteCoord);
                deleteKeys.Add(key);
                liveKeys.Remove(key);
            }
            foreach (var key in deleteKeys)
            {
                dataMap.Remove(key);
            }

            var staleKeys = dataMap.Keys.Where(key => !liveKeys.Contains(key)).ToList();
            foreach (var key in staleKeys)
            {
                dataMap.Remove(key);
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