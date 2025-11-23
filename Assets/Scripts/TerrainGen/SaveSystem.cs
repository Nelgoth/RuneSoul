using System.IO;
using UnityEngine;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;

/// <summary>
/// High-performance save system with binary format and async I/O
/// Replaces slow JSON serialization with fast binary format
/// </summary>
public static class SaveSystem 
{
    // Cache folder paths to avoid expensive Path.Combine operations
    private static string cachedWorldFolder = null;
    private static string cachedChunkFolder = null;
    private static readonly object saveLock = new object();
    private static Dictionary<Vector3Int, string> chunkFilePathCache = new Dictionary<Vector3Int, string>(500);
    private static bool ShouldLogChunkIO => World.Instance != null && World.Instance.Config != null && World.Instance.Config.enableChunkIOLogs;
    
    // Async save queue
    private static ConcurrentQueue<SaveOperation> saveQueue = new ConcurrentQueue<SaveOperation>();
    private static CancellationTokenSource cancellationToken;
    private static Task saveWorker;
    private static bool isInitialized = false;
    
    // Modification log for fast saves
    private static ChunkModificationLog modificationLog;
    
    // Save format preference
    public enum SaveFormat
    {
        JSON,
        Binary,
        BinaryCompressed
    }
    
    private static SaveFormat currentFormat = SaveFormat.BinaryCompressed;
    
    private struct SaveOperation
    {
        public ChunkData Data;
        public TaskCompletionSource<bool> Completion;
        public SaveFormat Format;
    }
    
    public static void Initialize()
    {
        if (isInitialized)
            return;
            
        cancellationToken = new CancellationTokenSource();
        saveWorker = Task.Run(() => SaveWorkerLoop(cancellationToken.Token));
        isInitialized = true;
        
        // Initialize modification log
        if (WorldSaveManager.Instance != null && !string.IsNullOrEmpty(WorldSaveManager.Instance.WorldSaveFolder))
        {
            try
            {
                modificationLog = new ChunkModificationLog(WorldSaveManager.Instance.WorldSaveFolder);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to initialize modification log: {ex.Message}");
            }
        }
        
        Debug.Log("[SaveSystem] Initialized with async I/O and binary format");
    }
    
    public static void Shutdown()
    {
        if (!isInitialized)
            return;
            
        Debug.Log("[SaveSystem] Shutting down, processing remaining saves...");
        
        // Wait for queue to empty
        int timeout = 0;
        while (!saveQueue.IsEmpty && timeout < 100)
        {
            Thread.Sleep(100);
            timeout++;
        }
        
        // Cancel worker
        cancellationToken?.Cancel();
        
        try
        {
            saveWorker?.Wait(5000); // Wait up to 5 seconds
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Save worker shutdown with exception: {ex.Message}");
        }
        
        // Close modification log
        modificationLog?.Close();
        modificationLog = null;
        
        isInitialized = false;
        Debug.Log("[SaveSystem] Shutdown complete");
    }
    
    private static async Task SaveWorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (saveQueue.TryDequeue(out SaveOperation operation))
                {
                    try
                    {
                        bool success = await SaveChunkDataInternalAsync(operation.Data, operation.Format);
                        operation.Completion?.TrySetResult(success);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error saving chunk {operation.Data.ChunkCoordinate}: {ex.Message}");
                        operation.Completion?.TrySetResult(false);
                    }
                }
                else
                {
                    // Queue empty, wait a bit
                    await Task.Delay(50, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in save worker loop: {ex.Message}");
                await Task.Delay(100, token);
            }
        }
    }
    
    public static void SetSaveFormat(SaveFormat format)
    {
        currentFormat = format;
        Debug.Log($"[SaveSystem] Save format changed to: {format}");
    }
    
    public static SaveFormat GetSaveFormat()
    {
        return currentFormat;
    }
    
    public static void LogModification(Vector3Int chunkCoord, Vector3Int voxelPos, bool isAdding, float densityChange = 0)
    {
        modificationLog?.LogModification(chunkCoord, voxelPos, isAdding, densityChange);
    }
    
    public static bool HasModifications(Vector3Int chunkCoord)
    {
        return modificationLog?.HasModifications(chunkCoord) ?? false;
    }
    
    public static void ClearChunkModifications(Vector3Int chunkCoord)
    {
        modificationLog?.ClearChunkModifications(chunkCoord);
    }
    
    private static string GetSaveFolder()
    {
        // Use cached path if available
        if (!string.IsNullOrEmpty(cachedChunkFolder))
            return cachedChunkFolder;
            
        if (WorldSaveManager.Instance == null || string.IsNullOrEmpty(WorldSaveManager.Instance.CurrentWorldId))
        {
            throw new Exception("WorldSaveManager not initialized");
        }
        
        // Cache the world folder path
        cachedWorldFolder = WorldSaveManager.Instance.WorldSaveFolder;
        if (!Directory.Exists(cachedWorldFolder))
        {
            if (ShouldLogChunkIO)
            {
                Debug.Log($"[WorldSaveManager] Creating world folder: {cachedWorldFolder}");
            }
            Directory.CreateDirectory(cachedWorldFolder);
        }

        // Cache the chunk folder path
        cachedChunkFolder = cachedWorldFolder + "/Chunks";
        if (!Directory.Exists(cachedChunkFolder))
        {
            if (ShouldLogChunkIO)
            {
                Debug.Log($"[WorldSaveManager] Creating chunks folder: {cachedChunkFolder}");
            }
            Directory.CreateDirectory(cachedChunkFolder);
        }

        return cachedChunkFolder;
    }

    private static string GetChunkFilePath(Vector3Int chunkCoord, SaveFormat format)
    {
        string folderPath = GetSaveFolder();
        string extension = format == SaveFormat.JSON ? ".json" : ".bin";
        string fileName = $"chunk_{chunkCoord.x}_{chunkCoord.y}_{chunkCoord.z}{extension}";
        return folderPath + "/" + fileName;
    }
    
    private static string GetChunkFilePathCached(Vector3Int chunkCoord)
    {
        // Check cache first
        if (chunkFilePathCache.TryGetValue(chunkCoord, out string cachedPath))
        {
            if (File.Exists(cachedPath))
                return cachedPath;
        }
            
        // Try binary format first, then JSON
        string binaryPath = GetChunkFilePath(chunkCoord, SaveFormat.Binary);
        if (File.Exists(binaryPath))
        {
            chunkFilePathCache[chunkCoord] = binaryPath;
            return binaryPath;
        }
        
        string jsonPath = GetChunkFilePath(chunkCoord, SaveFormat.JSON);
        if (File.Exists(jsonPath))
        {
            chunkFilePathCache[chunkCoord] = jsonPath;
            return jsonPath;
        }
        
        // Default to current format for new files
        string defaultPath = GetChunkFilePath(chunkCoord, currentFormat);
        chunkFilePathCache[chunkCoord] = defaultPath;
        return defaultPath;
    }
    
    /// <summary>
    /// Synchronous save (for backward compatibility)
    /// </summary>
    public static void SaveChunkData(ChunkData data)
    {
        if (!isInitialized)
            Initialize();
            
        SaveChunkDataAsync(data, currentFormat).Wait();
    }
    
    /// <summary>
    /// Async save - queues the save operation
    /// </summary>
    public static Task<bool> SaveChunkDataAsync(ChunkData data, SaveFormat? format = null)
    {
        if (!isInitialized)
            Initialize();
            
        var completion = new TaskCompletionSource<bool>();
        var operation = new SaveOperation
        {
            Data = data,
            Completion = completion,
            Format = format ?? currentFormat
        };
        
        saveQueue.Enqueue(operation);
        return completion.Task;
    }
    
    private static async Task<bool> SaveChunkDataInternalAsync(ChunkData data, SaveFormat format)
    {
        try
        {
            string filePath = GetChunkFilePath(data.ChunkCoordinate, format);
            string tempPath = filePath + ".tmp";

            if (ShouldLogChunkIO)
            {
                Debug.Log($"[SaveSystem] Saving chunk {data.ChunkCoordinate} to {filePath} (format: {format})");
            }
            
            // Prepare data for serialization
            data.PrepareForSerialization();
            
            byte[] dataToWrite = null;
            
            if (format == SaveFormat.JSON)
            {
                // Legacy JSON format
                string json = JsonUtility.ToJson(data, prettyPrint: false);
                dataToWrite = System.Text.Encoding.UTF8.GetBytes(json);
            }
            else
            {
                // Binary format
                bool compress = format == SaveFormat.BinaryCompressed;
                dataToWrite = BinaryChunkSerializer.Serialize(data, compress);
            }
            
            // Write to temp file first
            await File.WriteAllBytesAsync(tempPath, dataToWrite);

            // Atomic replace
            if (File.Exists(filePath))
                File.Delete(filePath);
                
            File.Move(tempPath, filePath);
            
            // Clear from modification log
            ClearChunkModifications(data.ChunkCoordinate);
            
            // Update cache
            chunkFilePathCache[data.ChunkCoordinate] = filePath;
            
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveSystem] Error saving chunk data: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Synchronous load (for backward compatibility)
    /// </summary>
    public static bool LoadChunkData(Vector3Int chunkCoord, ChunkData data)
    {
        return LoadChunkDataAsync(chunkCoord, data).Result;
    }
    
    /// <summary>
    /// Async load - performs I/O on background thread
    /// </summary>
    public static async Task<bool> LoadChunkDataAsync(Vector3Int chunkCoord, ChunkData data)
    {
        string filePath = GetChunkFilePathCached(chunkCoord);
        
        bool fileExists = File.Exists(filePath);
        if (ShouldLogChunkIO)
        {
            Debug.Log($"[SaveSystem] Loading chunk {chunkCoord} from {filePath} (exists={fileExists})");
        }

        if (!fileExists)
            return false;

        try
        {
            // Read file
            byte[] fileData = await File.ReadAllBytesAsync(filePath);
            
            // Ensure arrays are created
            data.EnsureArraysCreated();
            
            // Detect format
            bool isBinary = BinaryChunkSerializer.IsBinaryFormat(filePath);
            
            if (isBinary)
            {
                // Binary format
                bool success = BinaryChunkSerializer.Deserialize(fileData, data);
                if (!success)
                {
                    Debug.LogError($"[SaveSystem] Failed to deserialize binary chunk {chunkCoord}");
                    return false;
                }
            }
            else
            {
                // Legacy JSON format
                string json = System.Text.Encoding.UTF8.GetString(fileData);
                JsonUtility.FromJsonOverwrite(json, data);
                data.LoadFromSerialization();
            }
            
            // Apply any pending modifications from log
            if (modificationLog != null && modificationLog.HasModifications(chunkCoord))
            {
                var modifications = modificationLog.GetModifications(chunkCoord);
                foreach (var mod in modifications)
                {
                    // Apply modification to loaded data
                    // This would require access to World instance to apply properly
                    // For now, we just log it
                    if (ShouldLogChunkIO)
                    {
                        Debug.Log($"[SaveSystem] Chunk {chunkCoord} has {modifications.Count} pending modifications");
                    }
                }
            }
            
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveSystem] Error loading chunk {chunkCoord}: {e}");
            return false;
        }
    }
    
    /// <summary>
    /// Resets cache when changing worlds
    /// </summary>
    public static void ResetPathCache()
    {
        cachedWorldFolder = null;
        cachedChunkFolder = null;
        chunkFilePathCache.Clear();
        
        // Close and reinitialize modification log
        modificationLog?.Close();
        modificationLog = null;
        
        if (WorldSaveManager.Instance != null && !string.IsNullOrEmpty(WorldSaveManager.Instance.WorldSaveFolder))
        {
            try
            {
                modificationLog = new ChunkModificationLog(WorldSaveManager.Instance.WorldSaveFolder);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to reinitialize modification log: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Gets count of pending save operations
    /// </summary>
    public static int GetPendingSaveCount()
    {
        return saveQueue.Count;
    }
    
    /// <summary>
    /// Waits for all pending saves to complete
    /// </summary>
    public static async Task WaitForPendingSaves(int timeoutMs = 30000)
    {
        int elapsed = 0;
        while (!saveQueue.IsEmpty && elapsed < timeoutMs)
        {
            await Task.Delay(100);
            elapsed += 100;
        }
        
        if (!saveQueue.IsEmpty)
        {
            Debug.LogWarning($"[SaveSystem] Timeout waiting for saves, {saveQueue.Count} operations still pending");
        }
    }
}
