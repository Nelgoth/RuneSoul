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
    
    [Header("Chunk Data Sync Settings")]
    [SerializeField] private int chunkSyncBatchSize = 5;
    [SerializeField] private float chunkSyncInterval = 0.2f;
    [SerializeField] private int maxChunkDataSize = 65000; // Unity Netcode has ~65KB limit per RPC

    // Singleton pattern for easy access
    public static NetworkTerrainManager Instance { get; private set; }

    // Track chunk modifications
    private Dictionary<Vector3Int, ulong> lastChunkModifier = new Dictionary<Vector3Int, ulong>();
    private Dictionary<ulong, HashSet<Vector3Int>> playerModifiedChunks = new Dictionary<ulong, HashSet<Vector3Int>>();
    
    // Track modified chunks that need syncing to new clients
    private HashSet<Vector3Int> modifiedChunks = new HashSet<Vector3Int>();

    // Queue for terrain modifications to be synced
    private Queue<TerrainModification> pendingModifications = new Queue<TerrainModification>();
    private float lastSyncTime = 0f;
    
    // Track chunks synced to each client
    private Dictionary<ulong, HashSet<Vector3Int>> clientSyncedChunks = new Dictionary<ulong, HashSet<Vector3Int>>();
    
    // Queue for chunk data sync requests
    private Dictionary<ulong, Queue<Vector3Int>> clientChunkSyncQueues = new Dictionary<ulong, Queue<Vector3Int>>();
    private float lastChunkSyncTime = 0f;

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
    
    // Structure for chunk data sync
    private struct ChunkDataPacket : INetworkSerializable
    {
        public Vector3Int Coordinate;
        public bool HasModifications;
        public byte[] CompressedData;
        
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Coordinate);
            serializer.SerializeValue(ref HasModifications);
            
            if (serializer.IsWriter)
            {
                int length = CompressedData?.Length ?? 0;
                serializer.SerializeValue(ref length);
                if (length > 0)
                {
                    for (int i = 0; i < length; i++)
                    {
                        serializer.SerializeValue(ref CompressedData[i]);
                    }
                }
            }
            else
            {
                int length = 0;
                serializer.SerializeValue(ref length);
                if (length > 0)
                {
                    CompressedData = new byte[length];
                    for (int i = 0; i < length; i++)
                    {
                        serializer.SerializeValue(ref CompressedData[i]);
                    }
                }
            }
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
            
            // Initialize synced chunks tracking
            if (!clientSyncedChunks.ContainsKey(clientId))
            {
                clientSyncedChunks[clientId] = new HashSet<Vector3Int>();
            }
            
            // Initialize chunk sync queue
            if (!clientChunkSyncQueues.ContainsKey(clientId))
            {
                clientChunkSyncQueues[clientId] = new Queue<Vector3Int>();
            }
            
            // Queue all modified chunks for syncing to this client
            Debug.Log($"[NetworkTerrainManager] Queueing {modifiedChunks.Count} modified chunks for sync to client {clientId}");
            foreach (var chunkCoord in modifiedChunks)
            {
                clientChunkSyncQueues[clientId].Enqueue(chunkCoord);
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
        
        // Sync individual modifications
        if (Time.time - lastSyncTime > modificationSyncInterval && pendingModifications.Count > 0)
        {
            SyncTerrainModifications();
            lastSyncTime = Time.time;
        }
        
        // Sync full chunk data to clients
        if (Time.time - lastChunkSyncTime > chunkSyncInterval)
        {
            SyncChunkDataToClients();
            lastChunkSyncTime = Time.time;
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
        
        // Track this chunk as modified globally
        modifiedChunks.Add(chunkCoord);
            
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
    // NOTE: Now uses TerrainModificationBatch system for improved performance
    public void RequestTerrainModification(Vector3Int chunkCoord, Vector3Int voxelPos, bool isAdding)
    {
        // Handle single player mode or when not spawned
        if (NetworkManager.Singleton == null || !IsSpawned) 
        {
            // Direct application in single player
            // QueueVoxelUpdate now routes to batch system for better performance
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
            // QueueVoxelUpdate now uses batching system - modifications are accumulated and processed efficiently
            if (World.Instance != null)
            {
                World.Instance.QueueVoxelUpdate(chunkCoord, voxelPos, isAdding, true);
            }
            
            // Only send RPC if we're a connected client
            if (NetworkManager.Singleton.IsClient)
            {
                SubmitTerrainModificationServerRpc(chunkCoord, voxelPos, isAdding);
                // Removed excessive logging for performance
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
    
    /// <summary>
    /// Syncs full chunk data to clients (for modified chunks)
    /// </summary>
    private void SyncChunkDataToClients()
    {
        if (!IsServer || clientChunkSyncQueues.Count == 0)
            return;
            
        // Process each client's sync queue
        foreach (var clientEntry in clientChunkSyncQueues)
        {
            ulong clientId = clientEntry.Key;
            var syncQueue = clientEntry.Value;
            
            if (syncQueue.Count == 0)
                continue;
                
            // Process up to batchSize chunks per client per update
            int processed = 0;
            while (processed < chunkSyncBatchSize && syncQueue.Count > 0)
            {
                Vector3Int chunkCoord = syncQueue.Dequeue();
                
                // Skip if already synced
                if (clientSyncedChunks[clientId].Contains(chunkCoord))
                {
                    processed++;
                    continue;
                }
                
                // Try to sync this chunk
                if (TrySyncChunkToClient(clientId, chunkCoord))
                {
                    clientSyncedChunks[clientId].Add(chunkCoord);
                }
                else
                {
                    // Failed to sync, re-queue for later
                    syncQueue.Enqueue(chunkCoord);
                }
                
                processed++;
            }
        }
    }
    
    /// <summary>
    /// Attempts to sync a single chunk to a client
    /// </summary>
    private bool TrySyncChunkToClient(ulong clientId, Vector3Int chunkCoord)
    {
        try
        {
            // Check if chunk is loaded on server
            if (World.Instance == null || !World.Instance.TryGetChunk(chunkCoord, out Chunk chunk))
            {
                // Chunk not loaded, can't sync yet
                return false;
            }
            
            var chunkData = chunk.GetChunkData();
            if (chunkData == null || !chunkData.HasModifiedData)
            {
                // No modified data to sync
                return true; // Consider it synced
            }
            
            // Serialize chunk data
            byte[] compressedData = BinaryChunkSerializer.Serialize(chunkData, compress: true);
            
            // Check size limit
            if (compressedData.Length > maxChunkDataSize)
            {
                Debug.LogWarning($"Chunk {chunkCoord} data too large ({compressedData.Length} bytes), splitting not yet implemented");
                return true; // Skip for now
            }
            
            // Create packet
            var packet = new ChunkDataPacket
            {
                Coordinate = chunkCoord,
                HasModifications = true,
                CompressedData = compressedData
            };
            
            // Send to specific client
            ClientRpcParams clientParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new ulong[] { clientId }
                }
            };
            
            SyncChunkDataClientRpc(packet, clientParams);
            
            Debug.Log($"[NetworkTerrainManager] Synced chunk {chunkCoord} to client {clientId} ({compressedData.Length} bytes)");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to sync chunk {chunkCoord} to client {clientId}: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Client RPC to receive full chunk data
    /// </summary>
    [ClientRpc]
    private void SyncChunkDataClientRpc(ChunkDataPacket packet, ClientRpcParams clientRpcParams = default)
    {
        try
        {
            // Don't process on server
            if (IsServer)
                return;
                
            Debug.Log($"[NetworkTerrainManager] Client received chunk data for {packet.Coordinate} ({packet.CompressedData?.Length ?? 0} bytes)");
            
            if (!packet.HasModifications || packet.CompressedData == null)
                return;
                
            // Check if chunk is loaded
            if (World.Instance == null)
            {
                Debug.LogWarning("World instance not available to apply chunk sync");
                return;
            }
            
            // If chunk isn't loaded yet, we need to wait for it or cache this data
            if (!World.Instance.TryGetChunk(packet.Coordinate, out Chunk chunk))
            {
                // Cache for later application when chunk loads
                Debug.Log($"Chunk {packet.Coordinate} not yet loaded, caching sync data");
                // TODO: Implement caching mechanism for chunks not yet loaded
                return;
            }
            
            var chunkData = chunk.GetChunkData();
            if (chunkData == null)
            {
                Debug.LogError($"ChunkData null for loaded chunk {packet.Coordinate}");
                return;
            }
            
            // Deserialize and apply data
            bool success = BinaryChunkSerializer.Deserialize(packet.CompressedData, chunkData);
            if (success)
            {
                // Regenerate mesh with new data
                chunk.Generate(log: false, fullMesh: true, quickCheck: false);
                Debug.Log($"[NetworkTerrainManager] Successfully applied synced data to chunk {packet.Coordinate}");
            }
            else
            {
                Debug.LogError($"Failed to deserialize chunk data for {packet.Coordinate}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error applying chunk sync: {ex.Message}\n{ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// Client requests chunk data from server
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestChunkDataServerRpc(Vector3Int chunkCoord, ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        
        if (!clientChunkSyncQueues.ContainsKey(clientId))
        {
            clientChunkSyncQueues[clientId] = new Queue<Vector3Int>();
        }
        
        // Add to client's sync queue
        clientChunkSyncQueues[clientId].Enqueue(chunkCoord);
        
        Debug.Log($"[NetworkTerrainManager] Client {clientId} requested chunk {chunkCoord}");
    }
    
    /// <summary>
    /// Marks a chunk as modified and queues it for sync to all clients
    /// </summary>
    public void MarkChunkAsModified(Vector3Int chunkCoord)
    {
        if (!IsServer)
            return;
            
        modifiedChunks.Add(chunkCoord);
        
        // Queue for all connected clients
        foreach (var clientEntry in clientChunkSyncQueues)
        {
            if (!clientSyncedChunks[clientEntry.Key].Contains(chunkCoord))
            {
                clientEntry.Value.Enqueue(chunkCoord);
            }
        }
    }
}
