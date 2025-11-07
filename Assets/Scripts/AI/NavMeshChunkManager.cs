using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Manages per-chunk NavMeshData instances and schedules asynchronous rebuilds when
/// voxel terrain changes. Chunks call <see cref="RequestRebuild"/> after their mesh is
/// regenerated and <see cref="UnregisterChunk"/> when returned to the pool.
/// </summary>
public class NavMeshChunkManager : MonoBehaviour
{
    private const float DefaultRebuildCooldown = 0.35f;

    public static NavMeshChunkManager Instance { get; private set; }

    [SerializeField] private NavMeshChunkSettings settings;
    [Tooltip("Optional mask that limits which static objects are collected as build sources when assembling per-chunk nav-meshes.")]
    [SerializeField] private LayerMask includedLayers = ~0;
    [Tooltip("Enable to log rebuild scheduling and completion for debugging perf issues.")]
    [SerializeField] private bool verboseLogging;

    [Header("Debug / Telemetry")]
    [SerializeField] private bool drawChunkBounds = true;
    [SerializeField] private Color idleChunkColor = new Color(0f, 0.7f, 1f, 0.25f);
    [SerializeField] private Color rebuildingChunkColor = new Color(1f, 0.6f, 0f, 0.35f);
    [SerializeField] private bool showDurationLabels = true;
    [SerializeField] private bool logSlowRebuilds = true;
    [SerializeField] private float slowRebuildThresholdMs = 12f;
    [SerializeField] private int rollingSampleSize = 32;

    private readonly Queue<float> recentDurationsMs = new Queue<float>();
    private float rollingDurationSumMs;

    private readonly Dictionary<int, ChunkEntry> chunkEntries = new Dictionary<int, ChunkEntry>();
    private readonly Queue<ChunkEntry> buildQueue = new Queue<ChunkEntry>();
    private int activeBuilds;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("Duplicate NavMeshChunkManager detected, destroying duplicate.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        foreach (var entry in chunkEntries.Values)
        {
            CleanupEntry(entry);
        }
        chunkEntries.Clear();
        buildQueue.Clear();
    }

    private void Update()
    {
        ProcessQueue();
    }

    /// <summary>
    /// Registers a chunk so we can track and rebuild its nav-mesh data.
    /// </summary>
    public void RegisterChunk(Chunk chunk)
    {
        if (chunk == null)
        {
            return;
        }

        int id = chunk.GetInstanceID();
        if (chunkEntries.TryGetValue(id, out var existing))
        {
            if (existing.MarkedForRemoval)
            {
                existing.MarkedForRemoval = false;
                existing.IncludedLayers = includedLayers;
                existing.PendingBoundsValid = false;
                existing.PendingForce = false;
                existing.PendingAfterBuild = false;

                if (existing.Data == null)
                {
                    existing.Data = new NavMeshData(GetAgentTypeId());
                }

                if (!existing.Instance.valid)
                {
                    existing.Instance = NavMesh.AddNavMeshData(existing.Data);
                }

                if (verboseLogging)
                {
                    Debug.Log($"[NavMeshChunkManager] Reactivated chunk {GetCoordinateLabel(existing)}");
                }
            }
            return;
        }

        var entry = new ChunkEntry(chunk)
        {
            IncludedLayers = includedLayers
        };

        entry.Data = new NavMeshData(GetAgentTypeId());
        entry.Instance = NavMesh.AddNavMeshData(entry.Data);
        chunkEntries.Add(id, entry);

        if (verboseLogging)
        {
            Debug.Log($"[NavMeshChunkManager] Registered chunk {GetCoordinateLabel(entry)}");
        }
    }

    /// <summary>
    /// Removes chunk data, cancelling pending rebuilds and destroying the NavMesh instance.
    /// </summary>
    public void UnregisterChunk(Chunk chunk)
    {
        if (chunk == null)
        {
            return;
        }

        int id = chunk.GetInstanceID();
        if (!chunkEntries.TryGetValue(id, out var entry))
        {
            return;
        }

        entry.MarkedForRemoval = true;
        entry.PendingForce = false;
        entry.PendingBoundsValid = false;
        entry.PendingAfterBuild = false;

        // Remove any queued builds
        RemoveEntryFromQueue(entry);

        if (entry.Instance.valid)
        {
            entry.Instance.Remove();
            entry.Instance = default;
        }

        if (entry.CurrentOperation != null)
        {
            // Let the async op finish but mark removal so completion handler cleans up.
            return;
        }

        CleanupEntry(entry);
        chunkEntries.Remove(id);

        if (verboseLogging)
        {
            Debug.Log($"[NavMeshChunkManager] Unregistered chunk {GetCoordinateLabel(entry)}");
        }
    }

    /// <summary>
    /// Enqueue a nav-mesh rebuild for the provided chunk. Passing <paramref name="force"/> bypasses
    /// the per-chunk rebuild cooldown.
    /// </summary>
    public void RequestRebuild(Chunk chunk, bool force = false)
    {
        if (chunk == null)
        {
            return;
        }

        if (!chunkEntries.TryGetValue(chunk.GetInstanceID(), out var entry))
        {
            RegisterChunk(chunk);
            entry = chunkEntries[chunk.GetInstanceID()];
        }

        if (entry.MarkedForRemoval)
        {
            return;
        }

        entry.PendingBounds = entry.PendingBoundsValid
            ? Encapsulate(entry.PendingBounds, CalculateChunkBounds(chunk))
            : CalculateChunkBounds(chunk);
        entry.PendingBoundsValid = true;
        entry.PendingForce |= force;

        if (entry.CurrentOperation != null)
        {
            entry.PendingAfterBuild = true;
            return;
        }

        if (!entry.InQueue)
        {
            entry.InQueue = true;
            buildQueue.Enqueue(entry);
        }
    }

    private void ProcessQueue()
    {
        if (activeBuilds >= GetMaxConcurrentBuilds() || buildQueue.Count == 0)
        {
            return;
        }

        int processed = 0;
        int queueCount = buildQueue.Count;

        while (processed < queueCount && activeBuilds < GetMaxConcurrentBuilds() && buildQueue.Count > 0)
        {
            var entry = buildQueue.Dequeue();
            processed++;

            if (entry.MarkedForRemoval)
            {
                entry.InQueue = false;
                continue;
            }

            if (!entry.PendingBoundsValid)
            {
                entry.InQueue = false;
                continue;
            }

            if (!entry.PendingForce && Time.time < entry.NextAllowedBuildTime)
            {
                // Cooldown not satisfied yet - push to end of queue
                buildQueue.Enqueue(entry);
                continue;
            }

            entry.InQueue = false;
            StartRebuild(entry);
        }
    }

    private void StartRebuild(ChunkEntry entry)
    {
        if (entry == null || entry.MarkedForRemoval)
        {
            return;
        }

        if (!entry.Chunk || !entry.Chunk.gameObject.activeInHierarchy)
        {
            entry.PendingBoundsValid = false;
            entry.PendingForce = false;
            return;
        }

        var sources = entry.SourcesBuffer;
        sources.Clear();

        if (!CollectChunkSources(entry.Chunk, sources, entry.IncludedLayers))
        {
            // No geometry - ensure any existing navmesh instance is removed.
            if (entry.Instance.valid)
            {
                entry.Instance.Remove();
            }

            entry.PendingBoundsValid = false;
            entry.PendingForce = false;
            entry.PendingAfterBuild = false;
            entry.NextAllowedBuildTime = Time.time + GetRebuildCooldown();
            return;
        }

        // Ensure the instance is active in the NavMesh system
        if (!entry.Instance.valid)
        {
            entry.Instance = NavMesh.AddNavMeshData(entry.Data);
        }

        var buildSettings = GetBuildSettings();
        var bounds = entry.PendingBounds;

        entry.PendingBoundsValid = false;
        entry.PendingForce = false;
        entry.PendingAfterBuild = false;
        entry.BuildStartTime = Time.realtimeSinceStartup;
        entry.LastBuiltBounds = bounds;
        entry.CurrentOperation = NavMeshBuilder.UpdateNavMeshDataAsync(entry.Data, buildSettings, sources, bounds);

        if (entry.CurrentOperation == null)
        {
            Debug.LogError($"[NavMeshChunkManager] Failed to schedule navmesh build for chunk {GetCoordinateLabel(entry)}");
            entry.NextAllowedBuildTime = Time.time + GetRebuildCooldown();
            return;
        }

        if (entry.CompletionCallback == null)
        {
            entry.CompletionCallback = _ => OnBuildCompleted(entry);
        }

        entry.CurrentOperation.completed += entry.CompletionCallback;

        activeBuilds++;

        if (verboseLogging)
        {
            Debug.Log($"[NavMeshChunkManager] Building navmesh for {GetCoordinateLabel(entry)}, bounds {bounds}");
        }
    }

    private void OnBuildCompleted(ChunkEntry entry)
    {
        var operation = entry.CurrentOperation;
        if (operation != null && entry.CompletionCallback != null)
        {
            operation.completed -= entry.CompletionCallback;
        }

        activeBuilds = Mathf.Max(0, activeBuilds - 1);

        entry.LastBuildCompletedAt = Time.time;
        entry.NextAllowedBuildTime = Time.time + GetRebuildCooldown();
        entry.CurrentOperation = null;

        float durationMs = Mathf.Max(0f, (Time.realtimeSinceStartup - entry.BuildStartTime) * 1000f);
        entry.LastBuildDuration = durationMs;
        RecordDuration(durationMs);

        if (logSlowRebuilds && durationMs >= slowRebuildThresholdMs)
        {
            Debug.LogWarning($"[NavMeshChunkManager] Navmesh rebuild for {GetCoordinateLabel(entry)} took {durationMs:F1} ms");
        }

        if (entry.MarkedForRemoval)
        {
            CleanupEntry(entry);
            chunkEntries.Remove(entry.ChunkId);
            return;
        }

        if (entry.PendingBoundsValid || entry.PendingAfterBuild)
        {
            entry.PendingAfterBuild = false;
            if (!entry.InQueue)
            {
                entry.InQueue = true;
                buildQueue.Enqueue(entry);
            }
        }

        if (verboseLogging)
        {
            Debug.Log($"[NavMeshChunkManager] Navmesh build complete for {GetCoordinateLabel(entry)}");
        }
    }

    private void CleanupEntry(ChunkEntry entry)
    {
        if (entry.CurrentOperation != null && entry.CompletionCallback != null)
        {
            entry.CurrentOperation.completed -= entry.CompletionCallback;
        }
        entry.CurrentOperation = null;

        if (entry.Instance.valid)
        {
            entry.Instance.Remove();
            entry.Instance = default;
        }

        if (entry.Data != null)
        {
            Destroy(entry.Data);
            entry.Data = null;
        }
    }

    private void RemoveEntryFromQueue(ChunkEntry entry)
    {
        if (!entry.InQueue)
        {
            return;
        }

        entry.InQueue = false;

        if (buildQueue.Count == 0)
        {
            return;
        }

        int count = buildQueue.Count;
        for (int i = 0; i < count; i++)
        {
            var queued = buildQueue.Dequeue();
            if (!ReferenceEquals(queued, entry))
            {
                buildQueue.Enqueue(queued);
            }
        }
    }

    private int GetAgentTypeId()
    {
        return settings != null ? settings.AgentTypeId : 0;
    }

    private float GetRebuildCooldown()
    {
        return settings != null ? settings.RebuildCooldown : DefaultRebuildCooldown;
    }

    private int GetMaxConcurrentBuilds()
    {
        return settings != null ? settings.MaxConcurrentBuilds : 1;
    }

    private NavMeshBuildSettings GetBuildSettings()
    {
        var buildSettings = settings != null ? settings.BuildSettings : NavMesh.GetSettingsByID(GetAgentTypeId());
        if (buildSettings.agentTypeID == -1)
        {
            buildSettings = NavMesh.GetSettingsByID(0);
        }
        return buildSettings;
    }

    private static Bounds Encapsulate(Bounds original, Bounds other)
    {
        original.Encapsulate(other.min);
        original.Encapsulate(other.max);
        return original;
    }

    private Bounds CalculateChunkBounds(Chunk chunk)
    {
        var chunkData = chunk.GetChunkData();
        if (chunkData == null)
        {
            return new Bounds(chunk.transform.position, Vector3.zero);
        }

        float worldSize = chunkData.ChunkSize * chunkData.VoxelSize;
        Vector3 extents = new Vector3(worldSize, worldSize, worldSize) * 0.5f;
        Vector3 center = chunk.transform.position + extents;
        var bounds = new Bounds(center, extents * 2f);

        var padding = settings != null ? settings.BoundsPadding : Vector3.zero;
        if (padding != Vector3.zero)
        {
            bounds.Expand(padding * 2f);
        }

        return bounds;
    }

    private static bool CollectChunkSources(Chunk chunk, List<NavMeshBuildSource> sources, LayerMask layerMask)
    {
        if (chunk == null)
        {
            return false;
        }

        var meshFilter = chunk.GetComponent<MeshFilter>();
        var mesh = meshFilter != null ? meshFilter.sharedMesh : null;

        if (mesh == null || mesh.vertexCount == 0 || mesh.triangles == null || mesh.triangles.Length == 0)
        {
            return false;
        }

        var transform = meshFilter.transform.localToWorldMatrix;

        sources.Add(new NavMeshBuildSource
        {
            shape = NavMeshBuildSourceShape.Mesh,
            sourceObject = mesh,
            transform = transform,
            area = 0
        });

        // Collect additional static colliders within bounds to ensure objects placed on top of terrain are baked in.
        var collider = chunk.GetComponent<MeshCollider>();
        if (collider != null)
        {
            var bounds = collider.bounds;
            var colliders = Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, layerMask, QueryTriggerInteraction.Ignore);
            foreach (var col in colliders)
            {
                if (col.transform.IsChildOf(chunk.transform))
                {
                    continue; // already captured by chunk mesh
                }

                if (!col.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (col is MeshCollider meshCollider && meshCollider.sharedMesh != null)
                {
                    sources.Add(new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.Mesh,
                        sourceObject = meshCollider.sharedMesh,
                        transform = meshCollider.transform.localToWorldMatrix,
                        area = 0
                    });
                }
                else if (TryGetPrimitiveSource(col, out var primitive))
                {
                    sources.Add(primitive);
                }
            }
        }

        return sources.Count > 0;
    }

    private static bool TryGetPrimitiveSource(Collider collider, out NavMeshBuildSource source)
    {
        source = default;

        if (collider is BoxCollider box)
        {
            source = new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Box,
                size = box.size,
                transform = Matrix4x4.TRS(box.transform.position, box.transform.rotation, box.transform.lossyScale),
                area = 0
            };
            return true;
        }

        if (collider is CapsuleCollider capsule)
        {
            float height = Mathf.Max(capsule.height, capsule.radius * 2f);
            Vector3 size = Vector3.zero;
            size[capsule.direction] = height;
            int axisA = (capsule.direction + 1) % 3;
            int axisB = (capsule.direction + 2) % 3;
            size[axisA] = size[axisB] = capsule.radius * 2f;

            source = new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Capsule,
                size = size,
                transform = Matrix4x4.TRS(capsule.transform.position, capsule.transform.rotation, capsule.transform.lossyScale),
                area = 0
            };
            return true;
        }

        if (collider is SphereCollider sphere)
        {
            source = new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Sphere,
                size = Vector3.one * sphere.radius * 2f,
                transform = Matrix4x4.TRS(sphere.transform.position, sphere.transform.rotation, sphere.transform.lossyScale),
                area = 0
            };
            return true;
        }

        return false;
    }

    private void RecordDuration(float durationMs)
    {
        if (rollingSampleSize <= 0)
        {
            return;
        }

        recentDurationsMs.Enqueue(durationMs);
        rollingDurationSumMs += durationMs;

        while (recentDurationsMs.Count > rollingSampleSize)
        {
            rollingDurationSumMs -= recentDurationsMs.Dequeue();
        }
    }

    private static string GetCoordinateLabel(ChunkEntry entry)
    {
        if (entry?.Chunk == null)
        {
            return "<null>";
        }

        var data = entry.Chunk.GetChunkData();
        return data != null ? data.ChunkCoordinate.ToString() : entry.Chunk.transform.position.ToString();
    }

    public float AverageRebuildDurationMs => recentDurationsMs.Count == 0 ? 0f : rollingDurationSumMs / recentDurationsMs.Count;

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!drawChunkBounds)
        {
            return;
        }

        Gizmos.matrix = Matrix4x4.identity;

        foreach (var entry in chunkEntries.Values)
        {
            Bounds bounds = entry.PendingBoundsValid ? entry.PendingBounds : entry.LastBuiltBounds;
            if (bounds.size == Vector3.zero)
            {
                continue;
            }

            bool isBuilding = entry.CurrentOperation != null;
            Color color = isBuilding ? rebuildingChunkColor : idleChunkColor;

            Gizmos.color = color;
            Gizmos.DrawWireCube(bounds.center, bounds.size);

            if (isBuilding)
            {
                Gizmos.DrawCube(bounds.center, Vector3.one * 0.25f);
            }

            if (showDurationLabels && entry.LastBuildDuration > 0f)
            {
                UnityEditor.Handles.Label(bounds.center + Vector3.up * 0.5f,
                    $"{entry.LastBuildDuration:F1} ms");
            }
        }
    }
#endif

    private class ChunkEntry
    {
        public ChunkEntry(Chunk chunk)
        {
            Chunk = chunk;
            ChunkId = chunk != null ? chunk.GetInstanceID() : 0;
        }

        public Chunk Chunk { get; }
        public int ChunkId { get; }
        public NavMeshData Data;
        public NavMeshDataInstance Instance;
        public AsyncOperation CurrentOperation;
        public bool InQueue;
        public bool PendingForce;
        public bool PendingAfterBuild;
        public bool PendingBoundsValid;
        public bool MarkedForRemoval;
        public Bounds PendingBounds;
        public float LastBuildCompletedAt;
        public float NextAllowedBuildTime;
        public float BuildStartTime;
        public float LastBuildDuration;
        public Bounds LastBuiltBounds;
        public LayerMask IncludedLayers;
        public Action<AsyncOperation> CompletionCallback;
        public readonly List<NavMeshBuildSource> SourcesBuffer = new List<NavMeshBuildSource>(8);
    }
}

