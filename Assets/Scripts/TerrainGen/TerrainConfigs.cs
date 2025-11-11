// REPLACE ENTIRE FILE
using UnityEngine;

[CreateAssetMenu(fileName = "TerrainConfigs", menuName = "Voxel/TerrainConfigs")]
public class TerrainConfigs : ScriptableObject
{
    public enum LogVerbosity
    {
        ErrorsOnly,
        Warnings,
        Info,
        Debug
    }
    [Header("Core Settings")]
    [Tooltip("Size of each chunk in voxels")]
    public int chunkSize = 16;
    public float voxelSize = 1.0f;
    public float surfaceLevel = 0;

    [Header("Memory Management")]
    [Tooltip("Initial mesh pool size")]
    public int initialMeshPoolSize = 50;
    [Tooltip("Base vertex buffer size per mesh")]
    public int meshVertexBufferSize = 10000;
    [Tooltip("Maximum memory for mesh cache (MB)")]
    public long MaxMeshCacheSize = 256L * 1024L * 1024L;
    [Tooltip("Memory pressure threshold for cleanup")]
    [Range(0.5f, 0.95f)]
    public float memoryPressureThreshold = 0.8f;
    [Tooltip("Target memory usage after cleanup (% of max)")]
    [Range(0.3f, 0.7f)]
    public float targetMemoryUsage = 0.5f;
    
    [Header("Chunk Loading")]
    public int LoadRadius = 5;
    public int UnloadRadius = 7;
    public int VerticalLoadRadius = 2;
    public int VerticalUnloadRadius = 3;
    [Tooltip("Baseline chunk operations processed per frame during gameplay")]
    public int ChunksPerFrame = 64;
    [Tooltip("Minimum chunk operations processed per frame even when throttled")]
    public int minChunksPerFrame = 8;
    [Tooltip("FPS threshold where chunk processing begins to throttle")]
    public float chunkProcessingTargetFPS = 40f;
    [Tooltip("FPS threshold where chunk processing is reduced to the minimum throughput")]
    public float chunkProcessingMinimumFPS = 20f;
    [Tooltip("Multiplier applied to the base chunk throughput during the initial world load")]
    public float initialLoadChunkMultiplier = 12f;
    [Tooltip("Upper bound on chunk operations per frame during initial load")]
    public int maxInitialLoadChunksPerFrame = 2048;

    [Header("Generation")]
    [Tooltip("Base terrain height")]
    public float maxHeight = 64f;
    public int noiseSeed = 1234;
    public float noiseScale = 0.015f;
    public float frequency = 0.015f;
    [Tooltip("Max retries before quarantine")]
    public int MaxJobRetries = 3;

    [Header("Terrain Modification")]
    public float densityInfluenceRadius = 3.5f;
    public float baseDensityStrength = 3.0f;
    public float minDensityChangeThreshold = 0.01f;
    [Tooltip("Interval between updates")]
    public float updateInterval = 0.1f;
    public float voxelEpsilon = 0.01f;
    public float densityFalloffCutoff = 0.1f;
    [Tooltip("Maximum range at which players can modify terrain")]
    public float modificationRange = 10f;

    [Header("Mesh Generation")]
    [Range(0f, 180f)]
    public float normalSmoothingAngle = 120f;
    [Range(1f, 4f)] 
    public float normalSmoothingFactor = 2.5f;
    [Tooltip("Minimum area for valid triangle")]
    public float minTriangleArea = 1e-6f;
    [Tooltip("Maximum mesh bounds magnitude")]
    public float maxMeshBoundsMagnitude = 10000f;

    [Header("Performance")]
    [Tooltip("Maximum concurrent jobs")]
    public int MaxConcurrentJobs = 8;
    [Tooltip("Mesh generation batch size")]
    public int MeshBatchSize = 64;
    [Tooltip("Target FPS")]
    [Range(30, 120)]
    public int TargetFPS = 60;
    [Tooltip("Initial vertex buffer size per mesh")]
    

    [Header("Visual Settings")]
    public Material VoxelMaterial;
    public float triplanarScale = 1f;
    public float triplanarBlend = 1f;

    [Header("Terrain Analysis Settings")]
    public float AnalysisCacheExpirationDays = 30f; // Default to 30 days
    public bool PermanentlyStoreEmptyChunks = true; // Never expire empty chunk data

    [Header("Chunk Pool Settings")]
    [Tooltip("If enabled, the chunk pool size is calculated from the load radii and buffer multiplier")]
    public bool autoCalculateChunkPoolSize = true;
    [Tooltip("Manual chunk pool size when automatic calculation is disabled")]
    public int manualChunkPoolSize = 36000;
    [Tooltip("Applied when auto calculating to give extra pooled chunks")]
    [Range(1f, 2f)]
    public float chunkPoolBufferMultiplier = 1.1f;
    
    [Header("Debug Logging")]
    [Tooltip("Controls how verbose terrain analysis cache logging should be")]
    public LogVerbosity terrainCacheLogLevel = LogVerbosity.Warnings;
    [Tooltip("Log chunk save/load operations to the console")]
    public bool enableChunkIOLogs = false;
    [Tooltip("Enable detailed state transition logs for chunks")]
    public bool enableChunkStateLogs = false;
    [Tooltip("Enable verbose chunk lifecycle logs (initialization, loading, unloading)")]
    public bool enableChunkLifecycleLogs = false;
    [Tooltip("Enable detailed quick-check diagnostics during chunk loading")]
    public bool enableQuickCheckLogs = false;

    public int GetInitialLoadChunkBudget()
    {
        int baseChunks = Mathf.Max(minChunksPerFrame, ChunksPerFrame);
        int computed = Mathf.RoundToInt(baseChunks * Mathf.Max(1f, initialLoadChunkMultiplier));
        if (maxInitialLoadChunksPerFrame > 0)
        {
            computed = Mathf.Min(computed, Mathf.Max(baseChunks, maxInitialLoadChunksPerFrame));
        }
        return Mathf.Max(minChunksPerFrame, computed);
    }

    public int GetInitialChunkPoolSize()
    {
        if (!autoCalculateChunkPoolSize)
        {
            return Mathf.Max(1, manualChunkPoolSize);
        }

        int horizontalRadius = Mathf.Max(0, LoadRadius);
        int verticalRadius = Mathf.Max(0, VerticalLoadRadius);

        int horizontalCount = (horizontalRadius * 2) + 1;
        int verticalCount = (verticalRadius * 2) + 1;

        long baseCount = (long)horizontalCount * horizontalCount * verticalCount;
        baseCount = Mathf.CeilToInt(baseCount * Mathf.Max(1f, chunkPoolBufferMultiplier));

        return Mathf.Clamp((int)baseCount, 1, int.MaxValue);
    }
      
    private void OnValidate()
    {
        // Ensure reasonable minimums
        meshVertexBufferSize = Mathf.Max(1000, meshVertexBufferSize);
        minChunksPerFrame = Mathf.Max(1, minChunksPerFrame);
        ChunksPerFrame = Mathf.Max(minChunksPerFrame, ChunksPerFrame);
        chunkProcessingTargetFPS = Mathf.Max(10f, chunkProcessingTargetFPS);
        chunkProcessingMinimumFPS = Mathf.Clamp(chunkProcessingMinimumFPS, 1f, chunkProcessingTargetFPS - 1f);
        initialLoadChunkMultiplier = Mathf.Max(1f, initialLoadChunkMultiplier);
        maxInitialLoadChunksPerFrame = Mathf.Max(ChunksPerFrame, maxInitialLoadChunksPerFrame);
        manualChunkPoolSize = Mathf.Max(1, manualChunkPoolSize);
        
        // Scale vertex buffer with chunk size
        int expectedVertices = chunkSize * chunkSize * chunkSize / 4;
        meshVertexBufferSize = Mathf.Max(meshVertexBufferSize, expectedVertices);
    }
}