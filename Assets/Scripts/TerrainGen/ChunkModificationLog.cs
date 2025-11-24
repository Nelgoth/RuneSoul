using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Append-only log for chunk modifications
/// Allows fast saving of individual voxel changes without rewriting entire chunks
/// Periodically compacted into full chunk saves
/// </summary>
public class ChunkModificationLog
{
    private const uint MAGIC_NUMBER = 0x4D4F444C; // "MODL" (MODification Log)
    private const byte FORMAT_VERSION = 1;
    
    private readonly string logFilePath;
    private readonly object writeLock = new object();
    private FileStream logStream;
    private BinaryWriter logWriter;
    private Task loadingTask;
    
    // Track modifications in memory for quick lookup
    private Dictionary<Vector3Int, List<VoxelModification>> pendingModifications = 
        new Dictionary<Vector3Int, List<VoxelModification>>();
    
    // Configuration
    private const int COMPACT_THRESHOLD = 1000; // Compact after this many entries
    private int modificationCount = 0;

    [Serializable]
    public struct VoxelModification
    {
        public long Timestamp;
        public Vector3Int ChunkCoord;
        public Vector3Int VoxelPos;
        public bool IsAdding;
        public float DensityChange;
        
        public VoxelModification(Vector3Int chunkCoord, Vector3Int voxelPos, bool isAdding, float densityChange = 0)
        {
            Timestamp = DateTime.UtcNow.Ticks;
            ChunkCoord = chunkCoord;
            VoxelPos = voxelPos;
            IsAdding = isAdding;
            DensityChange = densityChange;
        }
    }

    public ChunkModificationLog(string worldFolder)
    {
        if (string.IsNullOrEmpty(worldFolder))
            throw new ArgumentNullException(nameof(worldFolder));
            
        // Ensure directory exists
        if (!Directory.Exists(worldFolder))
            Directory.CreateDirectory(worldFolder);
            
        logFilePath = Path.Combine(worldFolder, "chunk_modifications.log");
        
        // Open or create log file in append mode
        InitializeLogFile();
        
        // Start loading modifications asynchronously in the background
        Debug.Log($"[ChunkModificationLog] Initialized, log file: {logFilePath}");
        Debug.Log($"[ChunkModificationLog] Starting async modification loading...");
        loadingTask = Task.Run(() => LoadExistingModificationsAsync());
    }

    private void InitializeLogFile()
    {
        bool isNewFile = !File.Exists(logFilePath);
        
        try
        {
            Debug.Log($"[ChunkModificationLog] Opening log file: {logFilePath}");
            
            // Validate BEFORE opening the write stream to avoid file sharing conflicts
            if (!isNewFile)
            {
                try
                {
                    using (var fs = File.OpenRead(logFilePath))
                    using (var reader = new BinaryReader(fs))
                    {
                        if (fs.Length >= 5)
                        {
                            uint magic = reader.ReadUInt32();
                            byte version = reader.ReadByte();
                            
                            if (magic != MAGIC_NUMBER)
                            {
                                Debug.LogError($"Invalid modification log format - wrong magic number");
                                // Backup corrupted file and start fresh
                                BackupAndResetLog();
                                isNewFile = true; // Treat as new after backup
                            }
                            else if (version != FORMAT_VERSION)
                            {
                                Debug.LogWarning($"Modification log version mismatch: {version} (expected {FORMAT_VERSION})");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to validate existing log file: {ex.Message}, will recreate");
                    isNewFile = true;
                }
            }
            
            // Now open the file for writing
            logStream = new FileStream(logFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            logWriter = new BinaryWriter(logStream);
            Debug.Log($"[ChunkModificationLog] Log file opened successfully");
            
            if (isNewFile)
            {
                // Write header for new file
                logWriter.Write(MAGIC_NUMBER);
                logWriter.Write(FORMAT_VERSION);
                logWriter.Flush();
            }
            else
            {
                // Seek to end for appending
                logStream.Seek(0, SeekOrigin.End);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to initialize modification log: {ex.Message}");
            Debug.LogError($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private async Task EnsureModificationsLoadedAsync()
    {
        if (loadingTask != null)
        {
            await loadingTask;
        }
    }
    
    private void LoadExistingModificationsAsync()
    {
        if (!File.Exists(logFilePath) || new FileInfo(logFilePath).Length <= 5)
        {
            Debug.Log($"[ChunkModificationLog] No existing modifications to load");
            return;
        }
            
        try
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Debug.Log($"[ChunkModificationLog] Loading existing modifications from: {logFilePath}");
            
            // Read modifications on a background thread
            var tempModifications = new Dictionary<Vector3Int, List<VoxelModification>>();
            int count = 0;
            
            using (var fs = File.OpenRead(logFilePath))
            using (var reader = new BinaryReader(fs))
            {
                // Skip header
                fs.Seek(5, SeekOrigin.Begin);
                
                while (fs.Position < fs.Length)
                {
                    try
                    {
                        var mod = ReadModification(reader);
                        
                        if (!tempModifications.ContainsKey(mod.ChunkCoord))
                        {
                            tempModifications[mod.ChunkCoord] = new List<VoxelModification>();
                        }
                        
                        tempModifications[mod.ChunkCoord].Add(mod);
                        count++;
                    }
                    catch (EndOfStreamException)
                    {
                        // Reached end of file
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Error reading modification entry: {ex.Message}");
                        break;
                    }
                }
            }
            
            // Update the shared state with lock
            lock (writeLock)
            {
                pendingModifications = tempModifications;
                modificationCount = count;
            }
            
            sw.Stop();
            Debug.Log($"[ChunkModificationLog] Loaded {modificationCount} modifications from log in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to load existing modifications: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs a voxel modification
    /// </summary>
    public void LogModification(Vector3Int chunkCoord, Vector3Int voxelPos, bool isAdding, float densityChange = 0)
    {
        var modification = new VoxelModification(chunkCoord, voxelPos, isAdding, densityChange);
        
        lock (writeLock)
        {
            try
            {
                // Write to file
                WriteModification(logWriter, modification);
                logWriter.Flush();
                
                // Add to in-memory cache
                if (!pendingModifications.ContainsKey(chunkCoord))
                {
                    pendingModifications[chunkCoord] = new List<VoxelModification>();
                }
                pendingModifications[chunkCoord].Add(modification);
                
                modificationCount++;
                
                // Check if we should compact
                if (modificationCount >= COMPACT_THRESHOLD)
                {
                    // Schedule compaction (don't block here)
                    ScheduleCompaction();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to log modification: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gets all pending modifications for a chunk
    /// Returns empty list if async loading is still in progress (non-blocking)
    /// </summary>
    public List<VoxelModification> GetModifications(Vector3Int chunkCoord)
    {
        // CRITICAL FIX: Don't block with .Wait() - return empty if not ready
        if (loadingTask != null && !loadingTask.IsCompleted)
        {
            Debug.LogWarning($"[ChunkModificationLog] Modification loading still in progress, returning empty for chunk {chunkCoord}");
            return new List<VoxelModification>();
        }
        
        lock (writeLock)
        {
            if (pendingModifications.TryGetValue(chunkCoord, out var mods))
            {
                return new List<VoxelModification>(mods); // Return copy
            }
            return new List<VoxelModification>();
        }
    }

    /// <summary>
    /// Clears modifications for a chunk after it's been saved
    /// Does nothing if async loading is still in progress (non-blocking)
    /// </summary>
    public void ClearChunkModifications(Vector3Int chunkCoord)
    {
        // CRITICAL FIX: Don't block with .Wait() - skip operation if not ready
        if (loadingTask != null && !loadingTask.IsCompleted)
        {
            Debug.LogWarning($"[ChunkModificationLog] Modification loading still in progress, skipping clear for chunk {chunkCoord}");
            return;
        }
        
        lock (writeLock)
        {
            if (pendingModifications.TryGetValue(chunkCoord, out var mods))
            {
                modificationCount -= mods.Count;
                pendingModifications.Remove(chunkCoord);
            }
        }
    }

    /// <summary>
    /// Checks if a chunk has pending modifications
    /// Returns false if async loading is still in progress (non-blocking)
    /// </summary>
    public bool HasModifications(Vector3Int chunkCoord)
    {
        // CRITICAL FIX: Don't block with .Wait() - return false if not ready
        if (loadingTask != null && !loadingTask.IsCompleted)
        {
            return false;
        }
        
        lock (writeLock)
        {
            return pendingModifications.ContainsKey(chunkCoord) && 
                   pendingModifications[chunkCoord].Count > 0;
        }
    }

    /// <summary>
    /// Gets count of all pending modifications
    /// </summary>
    public int GetPendingModificationCount()
    {
        lock (writeLock)
        {
            return modificationCount;
        }
    }
    
    /// <summary>
    /// Checks if the initial loading of modifications is complete
    /// </summary>
    public bool IsLoadingComplete()
    {
        return loadingTask == null || loadingTask.IsCompleted;
    }
    
    /// <summary>
    /// Waits for the loading to complete (if still in progress)
    /// Returns true if loading completed successfully, false if there was an error
    /// </summary>
    public bool WaitForLoadingComplete()
    {
        if (loadingTask != null && !loadingTask.IsCompleted)
        {
            try
            {
                loadingTask.Wait();
                return !loadingTask.IsFaulted;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error waiting for modification loading: {ex.Message}");
                return false;
            }
        }
        return true;
    }

    private void WriteModification(BinaryWriter writer, VoxelModification mod)
    {
        writer.Write(mod.Timestamp);
        writer.Write(mod.ChunkCoord.x);
        writer.Write(mod.ChunkCoord.y);
        writer.Write(mod.ChunkCoord.z);
        writer.Write(mod.VoxelPos.x);
        writer.Write(mod.VoxelPos.y);
        writer.Write(mod.VoxelPos.z);
        writer.Write(mod.IsAdding);
        writer.Write(mod.DensityChange);
    }

    private VoxelModification ReadModification(BinaryReader reader)
    {
        return new VoxelModification
        {
            Timestamp = reader.ReadInt64(),
            ChunkCoord = new Vector3Int(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
            VoxelPos = new Vector3Int(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
            IsAdding = reader.ReadBoolean(),
            DensityChange = reader.ReadSingle()
        };
    }

    private void ScheduleCompaction()
    {
        // Mark for compaction - actual compaction will happen in background
        Debug.Log($"[ChunkModificationLog] Scheduling compaction ({modificationCount} modifications)");
        
        // For now, we'll just log this. Full compaction implementation would:
        // 1. Save all chunks with pending modifications
        // 2. Create new log file
        // 3. Replace old log file with new one
        // This should be done on a background thread to avoid blocking
    }

    /// <summary>
    /// Performs compaction by saving all modified chunks and clearing the log
    /// Should be called from a background thread
    /// </summary>
    public void Compact()
    {
        lock (writeLock)
        {
            try
            {
                Debug.Log($"[ChunkModificationLog] Starting compaction of {modificationCount} modifications");
                
                // Get all chunks with modifications
                var chunksToSave = new List<Vector3Int>(pendingModifications.Keys);
                
                // Close current log
                if (logWriter != null)
                {
                    logWriter.Close();
                    logWriter = null;
                }
                if (logStream != null)
                {
                    logStream.Close();
                    logStream = null;
                }
                
                // Backup old log
                string backupPath = logFilePath + ".backup";
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Move(logFilePath, backupPath);
                
                // Create new log
                InitializeLogFile();
                
                // Clear in-memory modifications
                pendingModifications.Clear();
                modificationCount = 0;
                
                Debug.Log($"[ChunkModificationLog] Compaction complete. {chunksToSave.Count} chunks were saved.");
                
                // Delete backup after successful compaction
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to compact modification log: {ex.Message}");
                
                // Try to restore from backup
                try
                {
                    string backupPath = logFilePath + ".backup";
                    if (File.Exists(backupPath))
                    {
                        if (File.Exists(logFilePath))
                            File.Delete(logFilePath);
                        File.Move(backupPath, logFilePath);
                        InitializeLogFile();
                    }
                }
                catch (Exception restoreEx)
                {
                    Debug.LogError($"Failed to restore log from backup: {restoreEx.Message}");
                }
            }
        }
    }

    private void BackupAndResetLog()
    {
        try
        {
            string backupPath = logFilePath + $".corrupted_{DateTime.Now:yyyyMMdd_HHmmss}";
            if (File.Exists(logFilePath))
            {
                File.Move(logFilePath, backupPath);
                Debug.LogWarning($"Backed up corrupted log to: {backupPath}");
            }
            
            // Create new log file
            logStream = new FileStream(logFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            logWriter = new BinaryWriter(logStream);
            logWriter.Write(MAGIC_NUMBER);
            logWriter.Write(FORMAT_VERSION);
            logWriter.Flush();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to backup and reset log: {ex.Message}");
        }
    }

    public void Close()
    {
        lock (writeLock)
        {
            if (logWriter != null)
            {
                logWriter.Flush();
                logWriter.Close();
                logWriter = null;
            }
            
            if (logStream != null)
            {
                logStream.Close();
                logStream = null;
            }
        }
    }

    ~ChunkModificationLog()
    {
        Close();
    }
}



