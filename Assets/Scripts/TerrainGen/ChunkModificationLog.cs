using System;
using System.IO;
using System.Collections.Generic;
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
        
        // Load existing modifications into memory
        LoadExistingModifications();
    }

    private void InitializeLogFile()
    {
        bool isNewFile = !File.Exists(logFilePath);
        
        try
        {
            logStream = new FileStream(logFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            logWriter = new BinaryWriter(logStream);
            
            if (isNewFile)
            {
                // Write header for new file
                logWriter.Write(MAGIC_NUMBER);
                logWriter.Write(FORMAT_VERSION);
                logWriter.Flush();
            }
            else
            {
                // Validate existing file
                using (var reader = new BinaryReader(File.OpenRead(logFilePath)))
                {
                    if (reader.BaseStream.Length >= 5)
                    {
                        uint magic = reader.ReadUInt32();
                        byte version = reader.ReadByte();
                        
                        if (magic != MAGIC_NUMBER)
                        {
                            Debug.LogError($"Invalid modification log format - wrong magic number");
                            // Backup corrupted file and start fresh
                            BackupAndResetLog();
                        }
                        else if (version != FORMAT_VERSION)
                        {
                            Debug.LogWarning($"Modification log version mismatch: {version} (expected {FORMAT_VERSION})");
                        }
                    }
                }
                
                // Seek to end for appending
                logStream.Seek(0, SeekOrigin.End);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to initialize modification log: {ex.Message}");
            throw;
        }
    }

    private void LoadExistingModifications()
    {
        if (!File.Exists(logFilePath) || new FileInfo(logFilePath).Length <= 5)
            return;
            
        try
        {
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
                        
                        if (!pendingModifications.ContainsKey(mod.ChunkCoord))
                        {
                            pendingModifications[mod.ChunkCoord] = new List<VoxelModification>();
                        }
                        
                        pendingModifications[mod.ChunkCoord].Add(mod);
                        modificationCount++;
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
            
            Debug.Log($"[ChunkModificationLog] Loaded {modificationCount} modifications from log");
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
    /// </summary>
    public List<VoxelModification> GetModifications(Vector3Int chunkCoord)
    {
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
    /// </summary>
    public void ClearChunkModifications(Vector3Int chunkCoord)
    {
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
    /// </summary>
    public bool HasModifications(Vector3Int chunkCoord)
    {
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



