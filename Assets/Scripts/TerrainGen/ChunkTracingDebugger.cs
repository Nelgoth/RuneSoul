using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Debug script to enable chunk tracing for debugging missing chunk issues.
/// Attach this to any GameObject in your scene to enable tracing controls.
/// </summary>
public class ChunkTracingDebugger : MonoBehaviour
{
    [Header("Chunk Tracing Controls")]
    [Tooltip("Enable tracing for all chunks affected by mining operations")]
    public bool autoTraceMining = false;
    
    [Header("Manual Chunk Tracing")]
    [Tooltip("Chunk coordinate to trace (X, Y, Z)")]
    public Vector3Int chunkToTrace = Vector3Int.zero;
    
    [Tooltip("World position to trace all affected chunks")]
    public Vector3 worldPositionToTrace = Vector3.zero;
    
    [Header("Chunk Diagnostics")]
    [Tooltip("Chunk coordinate to get diagnostics for")]
    public Vector3Int chunkToDiagnose = Vector3Int.zero;
    
    [Tooltip("World position to identify and trace the problematic chunk (use when you find a chunk that won't load)")]
    public Vector3 worldPosToIdentify = Vector3.zero;
    
    [Header("Missing Chunk Analysis")]
    [Tooltip("List of chunk coordinates around a hole (neighbors of the missing chunk). Use this to find which chunk is missing.")]
    public Vector3Int[] neighborChunks = new Vector3Int[0];
    
    private void Start()
    {
        if (World.Instance != null && autoTraceMining)
        {
            World.Instance.autoTraceMiningOperations = true;
            Debug.Log("[ChunkTracingDebugger] Auto-tracing enabled for all mining operations");
        }
    }
    
    private void Update()
    {
        // Enable tracing for specific chunk (press T key)
        if (Input.GetKeyDown(KeyCode.T))
        {
            if (chunkToTrace != Vector3Int.zero && World.Instance != null)
            {
                World.Instance.EnableChunkTracing(chunkToTrace);
                Debug.Log($"[ChunkTracingDebugger] Enabled tracing for chunk {chunkToTrace}");
            }
        }
        
        // Enable tracing for world position (press Y key)
        if (Input.GetKeyDown(KeyCode.Y))
        {
            if (worldPositionToTrace != Vector3.zero && World.Instance != null)
            {
                World.Instance.EnableTracingForMiningOperation(worldPositionToTrace);
                Debug.Log($"[ChunkTracingDebugger] Enabled tracing for mining operation at {worldPositionToTrace}");
            }
        }
        
        // Print trace summary (press U key)
        if (Input.GetKeyDown(KeyCode.U))
        {
            if (chunkToTrace != Vector3Int.zero && World.Instance != null)
            {
                World.Instance.PrintChunkTraceSummary(chunkToTrace);
            }
        }
        
        // Identify and trace chunk from world position (press I key)
        if (Input.GetKeyDown(KeyCode.I))
        {
            if (worldPosToIdentify != Vector3.zero && World.Instance != null)
            {
                World.Instance.IdentifyAndTraceChunkFromWorldPos(worldPosToIdentify);
            }
        }
        
        // Analyze missing chunks from neighbors (press M key)
        if (Input.GetKeyDown(KeyCode.M))
        {
            if (neighborChunks != null && neighborChunks.Length > 0 && World.Instance != null)
            {
                List<Vector3Int> chunks = new List<Vector3Int>(neighborChunks);
                World.Instance.AnalyzeMissingChunksFromNeighbors(chunks);
            }
        }
    }
    
    /// <summary>
    /// Call this from Unity console or other scripts to enable tracing for a chunk
    /// </summary>
    public void TraceChunk(Vector3Int chunkCoord)
    {
        if (World.Instance != null)
        {
            World.Instance.EnableChunkTracing(chunkCoord);
            Debug.Log($"[ChunkTracingDebugger] Enabled tracing for chunk {chunkCoord}");
        }
    }
    
    /// <summary>
    /// Call this from Unity console or other scripts to enable tracing for a mining operation
    /// </summary>
    public void TraceMiningOperation(Vector3 worldPos)
    {
        if (World.Instance != null)
        {
            World.Instance.EnableTracingForMiningOperation(worldPos);
            Debug.Log($"[ChunkTracingDebugger] Enabled tracing for mining operation at {worldPos}");
        }
    }
    
    /// <summary>
    /// Call this from Unity console or other scripts to print trace summary
    /// </summary>
    public void PrintSummary(Vector3Int chunkCoord)
    {
        if (World.Instance != null)
        {
            World.Instance.PrintChunkTraceSummary(chunkCoord);
        }
    }
    
    /// <summary>
    /// Enable tracing for a chunk and all its neighbors (useful for debugging problematic chunks)
    /// </summary>
    public void EnableTracingForChunkAndNeighbors()
    {
        if (World.Instance != null)
        {
            World.Instance.EnableTracingForChunkAndNeighbors(chunkToTrace);
        }
    }

    /// <summary>
    /// Print comprehensive diagnostics for a chunk
    /// </summary>
    public void PrintChunkDiagnostics()
    {
        if (World.Instance != null)
        {
            World.Instance.PrintChunkDiagnostics(chunkToDiagnose);
        }
    }
    
    /// <summary>
    /// Identify and trace a chunk from a world position (useful when you find a problematic chunk)
    /// </summary>
    public void IdentifyAndTraceFromWorldPos()
    {
        if (World.Instance != null && worldPosToIdentify != Vector3.zero)
        {
            World.Instance.IdentifyAndTraceChunkFromWorldPos(worldPosToIdentify);
        }
    }
    
    /// <summary>
    /// Identify and trace a chunk from a world position (callable from Unity console)
    /// </summary>
    public void IdentifyAndTraceFromWorldPos(Vector3 worldPos)
    {
        if (World.Instance != null)
        {
            World.Instance.IdentifyAndTraceChunkFromWorldPos(worldPos);
        }
    }
    
    /// <summary>
    /// Analyze missing chunks from neighbor chunks (callable from Unity console)
    /// </summary>
    public void AnalyzeMissingChunks()
    {
        if (World.Instance != null && neighborChunks != null && neighborChunks.Length > 0)
        {
            List<Vector3Int> chunks = new List<Vector3Int>(neighborChunks);
            World.Instance.AnalyzeMissingChunksFromNeighbors(chunks);
        }
        else
        {
            Debug.LogWarning("[ChunkTracingDebugger] Cannot analyze - neighborChunks array is empty or World.Instance is null");
        }
    }
    
    /// <summary>
    /// Quick method to analyze missing chunks with specific coordinates (callable from Unity console)
    /// Example: AnalyzeMissingChunksQuick(new Vector3Int(6,2,-1), new Vector3Int(6,2,0), new Vector3Int(5,1,0))
    /// </summary>
    public void AnalyzeMissingChunksQuick(params Vector3Int[] chunks)
    {
        if (World.Instance != null && chunks != null && chunks.Length > 0)
        {
            List<Vector3Int> chunkList = new List<Vector3Int>(chunks);
            World.Instance.AnalyzeMissingChunksFromNeighbors(chunkList);
        }
        else
        {
            Debug.LogWarning("[ChunkTracingDebugger] Cannot analyze - no chunks provided or World.Instance is null");
        }
    }
}

