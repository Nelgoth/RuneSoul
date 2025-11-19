using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.IO;

/// <summary>
/// Diagnostic tool to detect chunk-related corruption issues
/// </summary>
public class ChunkCorruptionDiagnostic : MonoBehaviour
{
    [Header("Diagnostics")]
    [SerializeField] private bool runOnStart = false;
    [SerializeField] private List<Vector3Int> specificChunksToCheck = new List<Vector3Int>();
    
    void Start()
    {
        if (runOnStart)
        {
            StartCoroutine(RunDiagnosticsAfterDelay());
        }
    }
    
    private System.Collections.IEnumerator RunDiagnosticsAfterDelay()
    {
        // Wait for world to initialize
        yield return new WaitForSeconds(2f);
        
        RunFullDiagnostics();
    }
    
    [ContextMenu("Run Full Diagnostics")]
    public void RunFullDiagnostics()
    {
        Debug.Log("=== CHUNK CORRUPTION DIAGNOSTICS ===");
        
        CheckWorldManagerIntegrity();
        CheckChunkPoolIntegrity();
        CheckTerrainCacheIntegrity();
        CheckChunkStateManagerIntegrity();
        CheckSavedChunkFiles();
        
        if (specificChunksToCheck.Count > 0)
        {
            CheckSpecificChunks();
        }
        
        Debug.Log("=== DIAGNOSTICS COMPLETE ===");
    }
    
    private void CheckWorldManagerIntegrity()
    {
        Debug.Log("--- World Manager Integrity Check ---");
        
        if (World.Instance == null)
        {
            Debug.LogError("World.Instance is NULL!");
            return;
        }
        
        var worldTransform = World.Instance.transform;
        Debug.Log($"World position: {worldTransform.position} (should be 0,0,0)");
        Debug.Log($"World rotation: {worldTransform.rotation.eulerAngles} (should be 0,0,0)");
        Debug.Log($"World scale: {worldTransform.localScale} (should be 1,1,1)");
        
        if (worldTransform.position != Vector3.zero)
        {
            Debug.LogWarning("World Manager position is not at origin! This could cause coordinate issues.");
        }
        
        if (worldTransform.rotation != Quaternion.identity)
        {
            Debug.LogWarning("World Manager is rotated! This will cause coordinate issues.");
        }
        
        if (worldTransform.localScale != Vector3.one)
        {
            Debug.LogWarning("World Manager scale is not 1,1,1! This will cause coordinate issues.");
        }
        
        // Check if World has duplicate chunks in dictionary
        var chunkDict = World.Instance.GetType()
            .GetField("chunks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(World.Instance) as Dictionary<Vector3Int, Chunk>;
            
        if (chunkDict != null)
        {
            Debug.Log($"World has {chunkDict.Count} chunks registered");
            
            // Check for null chunks
            int nullCount = chunkDict.Count(kvp => kvp.Value == null);
            if (nullCount > 0)
            {
                Debug.LogError($"Found {nullCount} NULL chunk references in World dictionary!");
            }
            
            // Check for duplicate GameObjects at same coordinate
            var positions = new Dictionary<Vector3, List<Vector3Int>>();
            foreach (var kvp in chunkDict)
            {
                if (kvp.Value != null)
                {
                    var pos = kvp.Value.transform.position;
                    if (!positions.ContainsKey(pos))
                        positions[pos] = new List<Vector3Int>();
                    positions[pos].Add(kvp.Key);
                }
            }
            
            int duplicates = positions.Count(kvp => kvp.Value.Count > 1);
            if (duplicates > 0)
            {
                Debug.LogError($"Found {duplicates} world positions with multiple chunks!");
                foreach (var kvp in positions.Where(p => p.Value.Count > 1))
                {
                    Debug.LogError($"Position {kvp.Key} has chunks: {string.Join(", ", kvp.Value)}");
                }
            }
        }
    }
    
    private void CheckChunkPoolIntegrity()
    {
        Debug.Log("--- Chunk Pool Integrity Check ---");
        
        if (ChunkPoolManager.Instance == null)
        {
            Debug.LogError("ChunkPoolManager.Instance is NULL!");
            return;
        }
        
        // Use reflection to check pool state
        var poolType = ChunkPoolManager.Instance.GetType();
        var availableChunks = poolType
            .GetField("availableChunks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(ChunkPoolManager.Instance) as Queue<Chunk>;
            
        if (availableChunks != null)
        {
            Debug.Log($"Chunk pool has {availableChunks.Count} available chunks");
            
            int nullInPool = availableChunks.Count(c => c == null);
            if (nullInPool > 0)
            {
                Debug.LogError($"Found {nullInPool} NULL chunks in pool!");
            }
        }
    }
    
    private void CheckTerrainCacheIntegrity()
    {
        Debug.Log("--- Terrain Analysis Cache Integrity Check ---");
        
        // Check cache file exists
        var cacheFilePath = TerrainAnalysisCache.GetCacheFilePath();
        if (File.Exists(cacheFilePath))
        {
            Debug.Log($"Cache file exists: {cacheFilePath}");
            var fileInfo = new FileInfo(cacheFilePath);
            Debug.Log($"Cache file size: {fileInfo.Length} bytes, last modified: {fileInfo.LastWriteTime}");
        }
        else
        {
            Debug.Log("No terrain analysis cache file found (this is normal for new worlds)");
        }
    }
    
    private void CheckChunkStateManagerIntegrity()
    {
        Debug.Log("--- Chunk State Manager Integrity Check ---");
        
        if (ChunkStateManager.Instance == null)
        {
            Debug.LogError("ChunkStateManager.Instance is NULL!");
            return;
        }
        
        var quarantined = ChunkStateManager.Instance.QuarantinedChunks;
        if (quarantined.Count > 0)
        {
            Debug.LogWarning($"Found {quarantined.Count} quarantined chunks:");
            foreach (var coord in quarantined.Take(10))
            {
                Debug.LogWarning($"  Quarantined: {coord}");
            }
        }
    }
    
    private void CheckSavedChunkFiles()
    {
        Debug.Log("--- Saved Chunk Files Check ---");
        
        if (WorldSaveManager.Instance == null)
        {
            Debug.LogWarning("WorldSaveManager.Instance is NULL - cannot check saved files");
            return;
        }
        
        string chunkFolder = WorldSaveManager.Instance.WorldSaveFolder + "/Chunks";
        if (!Directory.Exists(chunkFolder))
        {
            Debug.Log("No chunk save folder exists yet (this is normal for new worlds)");
            return;
        }
        
        var chunkFiles = Directory.GetFiles(chunkFolder, "chunk_*.json");
        Debug.Log($"Found {chunkFiles.Length} saved chunk files");
        
        // Parse coordinates and look for patterns
        var coords = new List<Vector3Int>();
        foreach (var file in chunkFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var parts = fileName.Replace("chunk_", "").Split('_');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out int x) &&
                int.TryParse(parts[1], out int y) &&
                int.TryParse(parts[2], out int z))
            {
                coords.Add(new Vector3Int(x, y, z));
            }
        }
        
        // Check for suspicious patterns
        var yNeg1 = coords.Where(c => c.y == -1).ToList();
        if (yNeg1.Count > 0)
        {
            Debug.Log($"Found {yNeg1.Count} saved chunks at Y=-1");
            
            // Check for the specific problematic pattern
            var xAbs5 = yNeg1.Where(c => Mathf.Abs(c.x) == 5 || Mathf.Abs(c.z) == 5).ToList();
            if (xAbs5.Count > 0)
            {
                Debug.Log($"Found {xAbs5.Count} saved chunks at Y=-1 with |X|=5 or |Z|=5:");
                foreach (var c in xAbs5.Take(10))
                {
                    Debug.Log($"  {c}");
                }
            }
        }
    }
    
    private void CheckSpecificChunks()
    {
        Debug.Log("--- Specific Chunks Check ---");
        
        foreach (var coord in specificChunksToCheck)
        {
            Debug.Log($"Checking chunk {coord}:");
            
            // Check if loaded
            bool isLoaded = World.Instance.TryGetChunk(coord, out Chunk chunk);
            Debug.Log($"  Is loaded: {isLoaded}");
            
            if (isLoaded && chunk != null)
            {
                Debug.Log($"  GameObject position: {chunk.transform.position}");
                Debug.Log($"  Expected position: {World.Instance.GetChunkWorldPosition(coord)}");
                Debug.Log($"  GameObject active: {chunk.gameObject.activeSelf}");
                Debug.Log($"  Has mesh filter: {chunk.GetComponent<MeshFilter>() != null}");
                Debug.Log($"  Has mesh renderer: {chunk.GetComponent<MeshRenderer>() != null}");
                
                var meshFilter = chunk.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    Debug.Log($"  Mesh vertex count: {meshFilter.sharedMesh.vertexCount}");
                    Debug.Log($"  Mesh triangle count: {meshFilter.sharedMesh.triangles.Length / 3}");
                }
                
                var chunkData = chunk.GetChunkData();
                if (chunkData != null)
                {
                    Debug.Log($"  ChunkData coordinate: {chunkData.ChunkCoordinate}");
                    Debug.Log($"  HasSavedData: {chunkData.HasSavedData}");
                    Debug.Log($"  IsSolid: {chunkData.IsSolidChunk}");
                    Debug.Log($"  IsEmpty: {chunkData.IsEmptyChunk}");
                    Debug.Log($"  DensityPoints created: {chunkData.DensityPoints.IsCreated}");
                }
            }
            
            // Check state
            var state = ChunkStateManager.Instance.GetChunkState(coord);
            Debug.Log($"  Chunk state: {state.Status}");
            
            // Check if has pending updates
            bool hasPending = World.Instance.HasPendingUpdates(coord);
            Debug.Log($"  Has pending updates: {hasPending}");
            
            // Check terrain cache
            if (TerrainAnalysisCache.TryGetAnalysis(coord, out var analysis))
            {
                Debug.Log($"  Cache analysis: IsSolid={analysis.IsSolid}, IsEmpty={analysis.IsEmpty}, WasModified={analysis.WasModified}");
            }
            else
            {
                Debug.Log($"  No terrain analysis cache entry");
            }
            
            // Check if save file exists
            if (WorldSaveManager.Instance != null)
            {
                string chunkFile = WorldSaveManager.Instance.WorldSaveFolder + $"/Chunks/chunk_{coord.x}_{coord.y}_{coord.z}.json";
                Debug.Log($"  Save file exists: {File.Exists(chunkFile)}");
            }
        }
    }
    
    [ContextMenu("Clear ALL Cache and Saved Data")]
    public void ClearAllCacheAndSavedData()
    {
#if UNITY_EDITOR
        if (!UnityEditor.EditorUtility.DisplayDialog("Clear All Data", 
            "This will clear terrain analysis cache and all saved chunk data. Continue?", 
            "Yes, Clear Everything", "Cancel"))
        {
            return;
        }
#endif
        
        // Clear terrain cache
        TerrainAnalysisCache.ResetCache();
        
        // Delete chunk save files
        if (WorldSaveManager.Instance != null)
        {
            string chunkFolder = WorldSaveManager.Instance.WorldSaveFolder + "/Chunks";
            if (Directory.Exists(chunkFolder))
            {
                Directory.Delete(chunkFolder, true);
                Debug.Log("Deleted all saved chunk files");
            }
        }
        
        Debug.Log("Cleared all cache and saved data. Restart the game for a completely fresh world.");
    }
    
    [ContextMenu("Reset World Manager Transform")]
    public void ResetWorldManagerTransform()
    {
        if (World.Instance != null)
        {
            World.Instance.transform.position = Vector3.zero;
            World.Instance.transform.rotation = Quaternion.identity;
            World.Instance.transform.localScale = Vector3.one;
            Debug.Log("Reset World Manager transform to origin");
        }
    }
}

