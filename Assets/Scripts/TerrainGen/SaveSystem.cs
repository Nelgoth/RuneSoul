// REPLACE SaveSystem.cs with this optimized version
using System.IO;
using UnityEngine;
using Unity.Mathematics;
using System;
using System.Linq;
using System.Collections.Generic;

public static class SaveSystem 
{
    // Cache folder paths to avoid expensive Path.Combine operations
    private static string cachedWorldFolder = null;
    private static string cachedChunkFolder = null;
    private static readonly object saveLock = new object();
    private static Dictionary<Vector3Int, string> chunkFilePathCache = new Dictionary<Vector3Int, string>(500);
    
    private static string GetSaveFolder()
    {
        // Use cached path if available
        if (!string.IsNullOrEmpty(cachedChunkFolder))
            return cachedChunkFolder;
            
        if (WorldSaveManager.Instance == null || string.IsNullOrEmpty(WorldSaveManager.Instance.CurrentWorldId))
        {
            throw new System.Exception("WorldSaveManager not initialized");
        }
        
        // Cache the world folder path
        cachedWorldFolder = WorldSaveManager.Instance.WorldSaveFolder;
        if (!Directory.Exists(cachedWorldFolder))
        {
            Debug.Log($"[WorldSaveManager] Creating world folder: {cachedWorldFolder}");
            Directory.CreateDirectory(cachedWorldFolder);
        }

        // Cache the chunk folder path
        cachedChunkFolder = cachedWorldFolder + "/Chunks";
        if (!Directory.Exists(cachedChunkFolder))
        {
            Debug.Log($"[WorldSaveManager] Creating chunks folder: {cachedChunkFolder}");
            Directory.CreateDirectory(cachedChunkFolder);
        }

        return cachedChunkFolder;
    }

    private static string GetChunkFilePath(Vector3Int chunkCoord)
    {
        // Check cache first
        if (chunkFilePathCache.TryGetValue(chunkCoord, out string cachedPath))
            return cachedPath;
            
        // Build path string directly instead of using Path.Combine
        string folderPath = GetSaveFolder();
        string fileName = $"chunk_{chunkCoord.x}_{chunkCoord.y}_{chunkCoord.z}.json";
        string fullPath = folderPath + "/" + fileName;
        
        // Cache for future use - limit cache size
        if (chunkFilePathCache.Count >= 500)
        {
            // Clear half the cache when it gets too large
            int toRemove = 250;
            var keys = chunkFilePathCache.Keys.Take(toRemove).ToList();
            foreach (var key in keys)
                chunkFilePathCache.Remove(key);
        }
        
        chunkFilePathCache[chunkCoord] = fullPath;
        return fullPath;
    }
    
    public static void SaveChunkData(ChunkData data)
    {
        string filePath = GetChunkFilePath(data.ChunkCoordinate);
        string tempPath = filePath + ".tmp";

        lock (saveLock)
        {
            try
            {
                string json = JsonUtility.ToJson(data, prettyPrint: false);
                File.WriteAllText(tempPath, json);

                if (File.Exists(filePath))
                    File.Delete(filePath);
                    
                File.Move(tempPath, filePath);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Error saving chunk data: {e.Message}");
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                throw;
            }
        }
    }

    public static bool LoadChunkData(Vector3Int chunkCoord, ChunkData data)
    {
        string filePath = GetChunkFilePath(chunkCoord);
        
        lock (saveLock)
        {
            if (!File.Exists(filePath))
                return false;

            try
            {
                string json = File.ReadAllText(filePath);
                data.EnsureArraysCreated();
                JsonUtility.FromJsonOverwrite(json, data);
                data.LoadFromSerialization();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Error loading chunk {chunkCoord}: {e}");
                return false;
            }
        }
    }
    
    // Add a method to reset cache when changing worlds
    public static void ResetPathCache()
    {
        cachedWorldFolder = null;
        cachedChunkFolder = null;
        chunkFilePathCache.Clear();
    }
}