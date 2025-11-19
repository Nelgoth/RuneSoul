using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using NelsUtils;
using System;

public class NetworkTerrainManager : NetworkBehaviour
{
    [Header("Terrain Modification Settings")]
    [SerializeField] private float modificationSyncInterval = 0.1f;
    [SerializeField] private int maxModificationsPerSync = 10;

    // Singleton pattern for easy access
    public static NetworkTerrainManager Instance { get; private set; }

    // Track chunk modifications
    private Dictionary<Vector3Int, ulong> lastChunkModifier = new Dictionary<Vector3Int, ulong>();
    private Dictionary<ulong, HashSet<Vector3Int>> playerModifiedChunks = new Dictionary<ulong, HashSet<Vector3Int>>();

    // Queue for terrain modifications to be synced
    private Queue<TerrainModification> pendingModifications = new Queue<TerrainModification>();
    private float lastSyncTime = 0f;

    // Structure to hold modification data
    private struct TerrainModification : INetworkSerializable
    {
        public Vector3Int ChunkCoord;
        public Vector3Int VoxelPosition;
        public bool IsAdding;
        public ulong PlayerId;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ChunkCoord);
            serializer.SerializeValue(ref VoxelPosition);
            serializer.SerializeValue(ref IsAdding);
            serializer.SerializeValue(ref PlayerId);
        }
    }

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

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // Subscribe to network events
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
        
        Debug.Log($"NetworkTerrainManager spawned. IsServer: {IsServer}, IsHost: {IsHost}");
    }

    public override void OnNetworkDespawn()
    {
        // Unsubscribe from network events
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
        }
        
        base.OnNetworkDespawn();
    }

    private void OnClientConnected(ulong clientId)
    {
        Debug.Log($"Client {clientId} connected to terrain network");
        
        if (IsServer)
        {
            // Initialize player modified chunks collection
            if (!playerModifiedChunks.ContainsKey(clientId))
            {
                playerModifiedChunks[clientId] = new HashSet<Vector3Int>();
            }
        }
    }

    private void OnClientDisconnect(ulong clientId)
    {
        if (IsServer)
        {
            // Clean up records for disconnected client
            if (playerModifiedChunks.TryGetValue(clientId, out var modifiedChunks))
            {
                // Clear player's chunk records
                playerModifiedChunks.Remove(clientId);
                
                // Remove from lastChunkModifier if this player was the last one
                foreach (var chunk in modifiedChunks)
                {
                    if (lastChunkModifier.TryGetValue(chunk, out var lastModifier) && lastModifier == clientId)
                    {
                        lastChunkModifier.Remove(chunk);
                    }
                }
            }
            
            Debug.Log($"Client {clientId} disconnected - cleaned up chunk modification records");
        }
    }

    private void Update()
    {
        // Only process terrain sync on server
        if (!IsServer) return;
        
        if (Time.time - lastSyncTime > modificationSyncInterval && pendingModifications.Count > 0)
        {
            SyncTerrainModifications();
            lastSyncTime = Time.time;
        }
    }

    // Called by clients to request a terrain modification
    [ServerRpc(RequireOwnership = false)]
    public void SubmitTerrainModificationServerRpc(Vector3Int chunkCoord, Vector3Int voxelPos, bool isAdding, ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        
        // Record this client as the last modifier of this chunk
        lastChunkModifier[chunkCoord] = clientId;
        
        // Add to player's modified chunks
        if (!playerModifiedChunks.ContainsKey(clientId))
        {
            playerModifiedChunks[clientId] = new HashSet<Vector3Int>();
        }
        playerModifiedChunks[clientId].Add(chunkCoord);
            
        // Queue modification for processing
        TerrainModification mod = new TerrainModification
        {
            ChunkCoord = chunkCoord,
            VoxelPosition = voxelPos,
            IsAdding = isAdding,
            PlayerId = clientId
        };
        
        pendingModifications.Enqueue(mod);
        
        // Apply immediately on server
        if (World.Instance != null)
        {
            World.Instance.QueueVoxelUpdate(chunkCoord, voxelPos, isAdding, true);
        }
        
        Debug.Log($"Server received modification from client {clientId} for chunk {chunkCoord}, voxel {voxelPos}");
    }

    // Called by ThirdPersonController or other components to request terrain modification
    public void RequestTerrainModification(Vector3Int chunkCoord, Vector3Int voxelPos, bool isAdding)
    {
        // Handle single player mode or when not spawned
        if (NetworkManager.Singleton == null || !IsSpawned) 
        {
            // Direct application in single player
            if (World.Instance != null)
            {
                World.Instance.QueueVoxelUpdate(chunkCoord, voxelPos, isAdding, true);
            }
            return;
        }
        
        // Networked mode
        try
        {
            // Apply immediately on the local client for responsiveness
            if (World.Instance != null)
            {
                World.Instance.QueueVoxelUpdate(chunkCoord, voxelPos, isAdding, true);
            }
            
            // Only send RPC if we're a connected client
            if (NetworkManager.Singleton.IsClient)
            {
                SubmitTerrainModificationServerRpc(chunkCoord, voxelPos, isAdding);
                Debug.Log($"Client requesting terrain modification at chunk {chunkCoord}, voxel {voxelPos}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error requesting terrain modification: {e.Message}");
            
            // Still try to apply locally even if network fails
            if (World.Instance != null)
            {
                World.Instance.QueueVoxelUpdate(chunkCoord, voxelPos, isAdding, true);
            }
        }
    }

    // NEW METHOD: Add this error-handling method to NetworkTerrainManager.cs
    private void SafeQueueModification(Vector3Int chunkCoord, Vector3Int voxelPos, bool isAdding)
    {
        if (World.Instance == null)
        {
            Debug.LogError("World.Instance is null - cannot queue modification");
            return;
        }
        
        try
        {
            World.Instance.QueueVoxelUpdate(chunkCoord, voxelPos, isAdding, true);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error queuing voxel update: {e.Message}");
        }
    }

    // REPLACE SyncTerrainModifications METHOD in NetworkTerrainManager.cs
    private void SyncTerrainModifications()
    {
        if (!IsServer) return;
        
        // Skip if there's nothing to sync
        if (pendingModifications.Count == 0) return;
        
        try
        {
            // Collect modifications up to our maximum per sync
            int count = Mathf.Min(pendingModifications.Count, maxModificationsPerSync);
            if (count <= 0) return;
            
            TerrainModification[] modifications = new TerrainModification[count];
            for (int i = 0; i < count; i++)
            {
                if (pendingModifications.Count > 0)
                {
                    modifications[i] = pendingModifications.Dequeue();
                }
                else
                {
                    // Handle unexpected queue emptying
                    count = i;
                    break;
                }
            }
            
            if (count > 0)
            {
                // Resize the array if we didn't fill it completely
                if (count < modifications.Length)
                {
                    System.Array.Resize(ref modifications, count);
                }
                
                // Send to all clients
                SyncTerrainModificationsClientRpc(modifications);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error during terrain sync: {e.Message}");
        }
    }

    // REPLACE SyncTerrainModificationsClientRpc METHOD in NetworkTerrainManager.cs
    [ClientRpc]
    private void SyncTerrainModificationsClientRpc(TerrainModification[] modifications, ClientRpcParams clientRpcParams = default)
    {
        if (modifications == null || modifications.Length == 0)
        {
            return;
        }
        
        foreach (var mod in modifications)
        {
            // Don't apply modifications that originated from this client (they're already applied locally)
            if (mod.PlayerId == NetworkManager.Singleton.LocalClientId)
                continue;
                
            // Apply the modification
            if (World.Instance != null)
            {
                try
                {
                    World.Instance.ApplyTerrainModification(mod.ChunkCoord, mod.VoxelPosition, mod.IsAdding);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error applying terrain modification: {e.Message}");
                    // Fall back to direct queue
                    SafeQueueModification(mod.ChunkCoord, mod.VoxelPosition, mod.IsAdding);
                }
            }
        }
    }
}