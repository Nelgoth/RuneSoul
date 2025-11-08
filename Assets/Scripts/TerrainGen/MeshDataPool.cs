// REPLACE MeshDataPool.cs content
using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using System;
using System.Threading;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections;

public class MeshDataPool : MonoBehaviour
{
    public static MeshDataPool Instance { get; private set; }

    private readonly object poolLock = new object();
    private readonly object disposalLock = new object();
    private Queue<Mesh> meshPool = new Queue<Mesh>();
    private Stack<NativeList<float3>> verticesPool;
    private Stack<NativeList<int>> trianglesPool;
    private bool isInitialized = false;

    // Memory management
    private long maxMemoryBytes;
    private long currentMemoryUsage;
    private float lastDefragTime;
    private float defragInterval = 300f; // 5 minutes

    // Disposal queue with thread-safe collections
    private class DisposalRequest
    {
        public Mesh mesh;
        public float timeQueued;
        public int retryCount;
    }
    private readonly ConcurrentQueue<DisposalRequest> disposalQueue = new ConcurrentQueue<DisposalRequest>();
    private const int MAX_DISPOSALS_PER_FRAME = 5;
    private const int MAX_RETRY_ATTEMPTS = 3;

    // Performance tracking
    private float lastFrameTime;
    private float averageFrameTime;
    private readonly Queue<float> frameTimeHistory = new Queue<float>(30);
    
    private bool initializationAttempted = false;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void Initialize()
    {
        if (isInitialized) return;
        
        if (World.Instance == null)
        {
            Debug.LogError("Cannot initialize MeshDataPool - World.Instance is null");
            return;
        }

        try 
        {
            maxMemoryBytes = World.Instance.Config.MaxMeshCacheSize;
            InitializePools();
            StartMemoryMonitoring();
            isInitialized = true;
            Debug.Log("MeshDataPool initialized successfully");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to initialize MeshDataPool: {e.Message}");
            OnDestroy();
        }
    }

    private void InitializePools()
    {
        if (isInitialized) return;
        
        try 
        {
            int poolSize = World.Instance.Config.initialMeshPoolSize;
            verticesPool = new Stack<NativeList<float3>>();
            trianglesPool = new Stack<NativeList<int>>();

            int chunkSize = World.Instance.Config.chunkSize;
            int baseBufferSize = World.Instance.Config.meshVertexBufferSize;
            int estimatedVertices = Math.Max(baseBufferSize, (chunkSize * chunkSize * chunkSize) / 4);
            
            lock (poolLock)
            {
                for (int i = 0; i < poolSize; i++)
                {
                    meshPool.Enqueue(new Mesh());
                    verticesPool.Push(new NativeList<float3>(estimatedVertices, Allocator.Persistent));
                    trianglesPool.Push(new NativeList<int>(estimatedVertices * 3, Allocator.Persistent));
                }
            }
            
            isInitialized = true;
            Debug.Log($"MeshDataPool initialized with {poolSize} meshes, buffer size {estimatedVertices}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to initialize MeshDataPool: {e.Message}");
            OnDestroy();
        }
    }

    private void Update()
    {
        // If not initialized and not yet attempted, try to initialize
        if (!isInitialized && !initializationAttempted && World.Instance != null)
        {
            initializationAttempted = true;
            Initialize();
        }

        if (isInitialized)
        {
            ProcessDisposalQueue();
            UpdateFrameTimeTracking();
            
            if (Time.time - lastDefragTime > defragInterval)
            {
                DefragmentPools();
            }
        }
    }

    private void StartMemoryMonitoring()
    {
        InvokeRepeating(nameof(CheckMemoryPressure), 10f, 10f);
    }

    private void CheckMemoryPressure()
    {
        if (currentMemoryUsage > maxMemoryBytes * World.Instance.Config.memoryPressureThreshold)
        {
            HandleHighMemoryPressure();
        }
    }

    public long GetCurrentMemoryUsage()
    {
        long totalMemory = Interlocked.Read(ref currentMemoryUsage);

        lock (poolLock)
        {
            foreach (var mesh in meshPool)
            {
                if (mesh != null)
                {
                    totalMemory += (mesh.vertexCount * sizeof(float) * 3) + // vertices
                                (mesh.triangles.Length * sizeof(int));     // triangles
                }
            }

            if (verticesPool != null)
            {
                foreach (var vertices in verticesPool)
                {
                    if (vertices.IsCreated)
                    {
                        totalMemory += vertices.Length * sizeof(float) * 3;
                    }
                }
            }

            if (trianglesPool != null)
            {
                foreach (var triangles in trianglesPool)
                {
                    if (triangles.IsCreated)
                    {
                        totalMemory += triangles.Length * sizeof(int);
                    }
                }
            }
        }

        return totalMemory;
    }

    private void UpdateFrameTimeTracking()
    {
        float currentFrameTime = Time.deltaTime;
        lastFrameTime = currentFrameTime;
        
        lock (frameTimeHistory)
        {
            frameTimeHistory.Enqueue(currentFrameTime);
            if (frameTimeHistory.Count > 30) frameTimeHistory.Dequeue();
            averageFrameTime = frameTimeHistory.Average();
        }
    }

    public int GetDynamicChunksPerFrame()
    {
        if (World.Instance == null || World.Instance.Config == null)
        {
            return 16;
        }

        if (World.Instance.IsInitialLoadInProgress)
        {
            return Mathf.Max(1, World.Instance.InitialLoadChunkBudget);
        }

        int baseChunks = Mathf.Max(1, World.Instance.Config.ChunksPerFrame);

        float averagedFrameTime;
        lock (frameTimeHistory)
        {
            averagedFrameTime = averageFrameTime > 0f ? averageFrameTime : Time.deltaTime;
        }

        float fps = averagedFrameTime > 1e-4f ? 1f / averagedFrameTime : 0f;
        int adjusted = baseChunks;

        if (fps < 40f)
        {
            float scale = Mathf.InverseLerp(15f, 40f, Mathf.Clamp(fps, 15f, 40f));
            float multiplier = Mathf.Lerp(0.3f, 1f, scale);
            adjusted = Mathf.Max(4, Mathf.RoundToInt(baseChunks * multiplier));
        }

        if (fps < 25f)
        {
            adjusted = Mathf.Min(adjusted, Mathf.Max(2, Mathf.RoundToInt(baseChunks * 0.35f)));
        }

        if (fps < 18f)
        {
            adjusted = Mathf.Min(adjusted, Mathf.Max(1, Mathf.RoundToInt(baseChunks * 0.2f)));
        }

        long maxBytes = Math.Max(1L, maxMemoryBytes);
        float memoryPressure = maxBytes > 0 ? (float)currentMemoryUsage / maxBytes : 0f;
        if (memoryPressure > World.Instance.Config.memoryPressureThreshold)
        {
            float pressureScale = Mathf.InverseLerp(World.Instance.Config.memoryPressureThreshold, 1f, Mathf.Clamp01(memoryPressure));
            adjusted = Mathf.Max(1, Mathf.RoundToInt(adjusted * Mathf.Lerp(1f, 0.5f, pressureScale)));
        }

        int upperBound = Mathf.Max(baseChunks, World.Instance.InitialLoadChunkBudget);
        return Mathf.Clamp(adjusted, 1, upperBound);
    }

    private void ProcessDisposalQueue()
    {
        if (disposalQueue.IsEmpty) return;

        int disposalsThisFrame = 0;
        float currentTime = Time.time;

        while (disposalsThisFrame < MAX_DISPOSALS_PER_FRAME && 
               disposalQueue.TryDequeue(out DisposalRequest request))
        {
            if (currentTime - request.timeQueued < 0.5f)
            {
                // Re-queue if not ready
                disposalQueue.Enqueue(request);
                continue;
            }

            try
            {
                if (request.mesh != null)
                {
                    DestroyImmediate(request.mesh);
                    Interlocked.Add(ref currentMemoryUsage, -EstimateMeshMemory(request.mesh));
                    disposalsThisFrame++;
                }
            }
            catch (Exception e)
            {
                if (request.retryCount < MAX_RETRY_ATTEMPTS)
                {
                    request.retryCount++;
                    request.timeQueued = currentTime;
                    disposalQueue.Enqueue(request);
                }
                Debug.LogError($"Error disposing mesh: {e.Message}");
            }
        }
    }

    private void DefragmentPools()
    {
        lock (poolLock)
        {
            var newMeshPool = new Queue<Mesh>();
            var newVerticesPool = new Stack<NativeList<float3>>();
            var newTrianglesPool = new Stack<NativeList<int>>();

            // Clean up invalid entries
            foreach (var mesh in meshPool)
            {
                if (mesh != null && mesh.vertexCount == 0)
                    newMeshPool.Enqueue(mesh);
                else if (mesh != null)
                    QueueMeshForDisposal(mesh);
            }

            // Ensure minimum pool size
            int minPoolSize = World.Instance.Config.initialMeshPoolSize / 2;
            int estimatedVertices = World.Instance.Config.meshVertexBufferSize;

            while (newMeshPool.Count < minPoolSize)
                newMeshPool.Enqueue(new Mesh());

            while (newVerticesPool.Count < minPoolSize)
                newVerticesPool.Push(new NativeList<float3>(estimatedVertices, Allocator.Persistent));

            while (newTrianglesPool.Count < minPoolSize)
                newTrianglesPool.Push(new NativeList<int>(estimatedVertices * 3, Allocator.Persistent));

            meshPool = newMeshPool;
            verticesPool = newVerticesPool;
            trianglesPool = newTrianglesPool;
        }
        
        lastDefragTime = Time.time;
    }

    private long EstimateMeshMemory(Mesh mesh)
    {
        if (mesh == null) return 0;
        return (mesh.vertexCount * sizeof(float) * 3) + // vertices
               (mesh.triangles.Length * sizeof(int));    // triangles
    }

    private void QueueMeshForDisposal(Mesh mesh)
    {
        if (mesh != null)
        {
            disposalQueue.Enqueue(new DisposalRequest 
            { 
                mesh = mesh, 
                timeQueued = Time.time,
                retryCount = 0
            });
        }
    }

    public Mesh GetMesh()
    {
        if (!isInitialized)
        {
            Debug.LogError("Attempting to get mesh before initialization!");
            Initialize();
            if (!isInitialized)
            {
                return new Mesh();
            }
        }

        Mesh mesh = null;
        
        lock (poolLock)
        {
            if (meshPool.Count > 0)
            {
                while (meshPool.Count > 0 && mesh == null)
                {
                    try
                    {
                        mesh = meshPool.Dequeue();
                        
                        // Verify the mesh is still valid
                        if (mesh == null || mesh.vertexCount > 0)
                        {
                            // If mesh is null or not empty, discard it
                            mesh = null;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Error popping mesh from stack: {e.Message}");
                        mesh = null;
                    }
                }
            }

            // Create new mesh if none available
            if (mesh == null)
            {
                try
                {
                    // Try to create a new mesh, tracking memory usage
                    Interlocked.Add(ref currentMemoryUsage, 4096); // Base allocation size
                    mesh = new Mesh();
                }
                catch (OutOfMemoryException oom)
                {
                    Debug.LogError($"Out of memory while creating mesh: {oom.Message}");
                    // Force clear some memory
                    HandleHighMemoryPressure();
                    
                    // Create a minimal mesh as fallback
                    try
                    {
                        mesh = new Mesh();
                        mesh.name = "Fallback_OOM";
                        Interlocked.Add(ref currentMemoryUsage, 1024);
                    }
                    catch
                    {
                        Debug.LogError("Critical memory shortage - returning null mesh");
                        return null;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error creating new mesh: {e.Message}");
                    return new Mesh();
                }
            }
        }
        
        return mesh;
    }

    public void ReturnMesh(Mesh mesh)
    {
        if (mesh == null)
        {
            Debug.LogError("[PoolManager] Attempted to return null mesh!");
            return;
        }

        try
        {
            lock (poolLock)
            {
                long meshMemory = EstimateMeshMemory(mesh);
                
                // Clear the mesh before returning it to the pool
                mesh.Clear(false);
                mesh.name = "PooledMesh";
                
                // Check if we're under memory pressure
                if (Interlocked.Read(ref currentMemoryUsage) + meshMemory < maxMemoryBytes)
                {
                    meshPool.Enqueue(mesh);
                    Interlocked.Add(ref currentMemoryUsage, meshMemory);
                }
                else
                {
                    QueueMeshForDisposal(mesh);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error returning mesh to pool: {e.Message}\n{e.StackTrace}");
            // Try to dispose directly in case of error
            try
            {
                if (mesh != null)
                {
                    DestroyImmediate(mesh);
                }
            }
            catch {}
        }
    }

    private void HandleHighMemoryPressure()
    {
        lock (poolLock)
        {
            float targetMemory = maxMemoryBytes * World.Instance.Config.targetMemoryUsage;
            
            while (currentMemoryUsage > targetMemory && meshPool.Count > 0)
            {
                var mesh = meshPool.Dequeue();
                if (mesh != null) QueueMeshForDisposal(mesh);
            }
        }
    }

    private void OnDestroy()
    {
        lock (poolLock)
        {
            while (verticesPool?.Count > 0)
            {
                var list = verticesPool.Pop();
                if (list.IsCreated) list.Dispose();
            }

            while (trianglesPool?.Count > 0)
            {
                var list = trianglesPool.Pop();
                if (list.IsCreated) list.Dispose();
            }
        }

        lock (disposalLock)
        {
            while (disposalQueue.TryDequeue(out var request))
            {
                if (request.mesh != null)
                    DestroyImmediate(request.mesh);
            }
        }

        lock (poolLock)
        {
            meshPool.Clear();
        }
    }

    private void OnApplicationQuit()
    {
        OnDestroy();
    }
}