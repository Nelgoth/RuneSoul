using System.Collections.Generic;
using UnityEngine;
using NelsUtils;
using System;
using Unity.Netcode;
using System.Linq;

public class ChunkPoolManager : MonoBehaviour {
    public static ChunkPoolManager Instance { get; private set; }
    public bool IsInitialized { get; private set; }
    private Queue<Chunk> chunkPool = new Queue<Chunk>();
    private Stack<Chunk> availableChunks = new Stack<Chunk>();
    private Transform poolContainer;
    public int initialPoolSize = 10;

    private void Awake()
    {
        Debug.Log($"[ChunkPool] Awake called, initialPoolSize: {initialPoolSize}");
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Subscribe to scene unload event
        UnityEngine.SceneManagement.SceneManager.sceneUnloaded += OnSceneUnloaded;

        // Create a container under this manager
        poolContainer = new GameObject("ChunkPoolContainer").transform;
        poolContainer.SetParent(transform, false);
        
        // Create initial pool
        for (int i = 0; i < initialPoolSize; i++)
        {
            GameObject chunkObject = new GameObject($"PooledChunk_{i}");
            // Put it under the poolContainer
            chunkObject.transform.SetParent(poolContainer, false);
            chunkObject.SetActive(false);

            // Minimal components: only a `Chunk`
            Chunk newChunk = chunkObject.AddComponent<Chunk>();
            availableChunks.Push(newChunk);
        }

        IsInitialized = true;
    }


    private void InitializeChunk(GameObject chunkObject)
    {
        if (chunkObject == null) return;

        try
        {
            // Add core components first
            if (!chunkObject.TryGetComponent<Chunk>(out _))
                chunkObject.AddComponent<Chunk>();
                
            if (!chunkObject.TryGetComponent<MeshFilter>(out _))
                chunkObject.AddComponent<MeshFilter>();
                
            if (!chunkObject.TryGetComponent<MeshRenderer>(out _))
                chunkObject.AddComponent<MeshRenderer>();
                
            if (!chunkObject.TryGetComponent<MeshCollider>(out _))
                chunkObject.AddComponent<MeshCollider>();
/*
            // Add network components last
            if (NetworkManager.Singleton != null)
            {
                if (!chunkObject.TryGetComponent<NetworkObject>(out _))
                    chunkObject.AddComponent<NetworkObject>();
                    
                if (!chunkObject.TryGetComponent<NetworkChunkSync>(out _))
                    chunkObject.AddComponent<NetworkChunkSync>();
            }
*/
        }
        catch (Exception e)
        {
            Debug.LogError($"Error initializing chunk components: {e.Message}\n{e.StackTrace}");
            throw;
        }
    }

    public Chunk GetChunk()
    {
        if (!IsInitialized)
        {
            Debug.LogError("ChunkPoolManager not initialized!");
            return null;
        }

        // 1) Try from `availableChunks`
        Chunk chunk = null;
        while (availableChunks.Count > 0 && chunk == null)
        {
            chunk = availableChunks.Pop();
            if (chunk == null || chunk.gameObject == null)
                chunk = null; // skip destroyed
        }

        // 2) If still null, instantiate a new one
        if (chunk == null)
        {
            Debug.Log("Creating new chunk as none available in pool");
            // This must come from your chunkPrefab if you have one,
            // or a plain GameObject with a Chunk script
            GameObject chunkObject = Instantiate(World.Instance.chunkPrefab);

            // Make sure it's under poolContainer
            chunkObject.transform.SetParent(poolContainer, false);
            chunkObject.SetActive(false);

            chunk = chunkObject.GetComponent<Chunk>();
            if (chunk == null)
            {
                Debug.LogError("Failed to get Chunk component");
                Destroy(chunkObject);
                return null;
            }
        }

        // Ensure it's under poolContainer, inactive
        chunk.transform.SetParent(poolContainer, false);
        chunk.gameObject.SetActive(false);

        // Return for further configuration (World can re-parent it if desired)
        return chunk;
    }

    private void VerifyChunkComponents(GameObject chunkObject)
    {
        var components = chunkObject.GetComponents<Component>();
        Debug.Log($"Components on {chunkObject.name}:");
        foreach (var comp in components)
        {
            Debug.Log($"- {comp.GetType().Name}");
        }

        var existingComponents = new HashSet<System.Type>(
            chunkObject.GetComponents<Component>().Select(c => c.GetType())
        );

        // Add core components first
        if (!existingComponents.Contains(typeof(Chunk)))
        {
            Debug.Log($"Adding missing Chunk component to {chunkObject.name}");
            chunkObject.AddComponent<Chunk>();
        }

        if (!existingComponents.Contains(typeof(MeshFilter)))
        {
            Debug.Log($"Adding missing MeshFilter component to {chunkObject.name}");
            chunkObject.AddComponent<MeshFilter>();
        }

        if (!existingComponents.Contains(typeof(MeshRenderer)))
        {
            Debug.Log($"Adding missing MeshRenderer component to {chunkObject.name}");
            chunkObject.AddComponent<MeshRenderer>();
        }

        if (!existingComponents.Contains(typeof(MeshCollider)))
        {
            Debug.Log($"Adding missing MeshCollider component to {chunkObject.name}");
            chunkObject.AddComponent<MeshCollider>();
        }
/*
        // Add network components last
        if (NetworkManager.Singleton != null)
        {
            if (!existingComponents.Contains(typeof(NetworkObject)))
            {
                Debug.Log($"Adding missing NetworkObject component to {chunkObject.name}");
                chunkObject.AddComponent<NetworkObject>();
            }
            
            if (!existingComponents.Contains(typeof(NetworkChunkSync)))
            {
                Debug.Log($"Adding missing NetworkChunkSync component to {chunkObject.name}");
                chunkObject.AddComponent<NetworkChunkSync>();
            }
        }
*/
    }

    private void PrepareChunkForUse(Chunk chunk)
    {
        if (chunk == null) return;

        try
        {
            // Ensure the object is properly set up
            chunk.gameObject.SetActive(false); // Deactivate first to avoid any premature updates
            chunk.transform.parent = null; // Detach from pool container
            
            // Initialize or verify components
            InitializeChunk(chunk.gameObject);

            // Now activate
            chunk.gameObject.SetActive(true);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error preparing chunk for use: {e.Message}\n{e.StackTrace}");
            throw;
        }
    }

    public void ReturnChunk(Chunk chunk)
    {
        if (chunk == null)
        {
            Debug.LogError("[PoolManager] Attempted to return null chunk!");
            return;
        }

        try
        {
            // 1) Complete all jobs
            chunk.CompleteAllJobs();
/*
            // 2) If it has a spawned NetworkObject (on the server), despawn without destruction
            var netObj = chunk.GetComponent<NetworkObject>();
            if (netObj != null && netObj.IsSpawned && 
                NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                netObj.Despawn(false);
            }

            // 3) Now remove any Netcode components
            if (netObj != null) DestroyImmediate(netObj);
            var netSync = chunk.GetComponent<NetworkChunkSync>();
            if (netSync != null) Destroy(netSync);
*/
            // 4) Re-parent to pool container
            chunk.transform.SetParent(poolContainer, false);

            // 5) Reset the chunk for next usage
            chunk.ResetChunk();

            // 6) Deactivate, then push onto the pool
            chunk.gameObject.SetActive(false);
            availableChunks.Push(chunk);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error returning chunk to pool: {e.Message}\n{e.StackTrace}");
            // Possibly quarantine, etc.
        }
    }

    public int GetAvailableCount()
    {
        return availableChunks.Count + chunkPool.Count;
    }

    private void OnSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
    {
        // Return all chunks to pool
        var chunks = FindObjectsByType<Chunk>(FindObjectsSortMode.None);
        foreach (var chunk in chunks)
        {
            if (chunk != null)
            {
                ReturnChunk(chunk);
            }
        }

        // Clear pool
        availableChunks.Clear();
    }

    public void ForceCleanup()
    {
        Debug.Log("Force cleaning ChunkPoolManager...");
        
        // Return all chunks to pool and destroy them
        var chunks = FindObjectsByType<Chunk>(FindObjectsSortMode.None);
        foreach (var chunk in chunks)
        {
            if (chunk != null)
            {
                chunk.CompleteAllJobs();
                Destroy(chunk.gameObject);
            }
        }

        // Clear the pool
        while (availableChunks.Count > 0)
        {
            var chunk = availableChunks.Pop();
            if (chunk != null)
            {
                Destroy(chunk.gameObject);
            }
        }

        // Cleanup pool container
        if (poolContainer != null)
        {
            Destroy(poolContainer.gameObject);
        }

        IsInitialized = false;
        Debug.Log("ChunkPoolManager cleanup complete");
    }

    private void OnDestroy()
    {
        UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= OnSceneUnloaded;
    }
}