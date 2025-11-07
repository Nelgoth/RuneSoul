using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Shared configuration for chunk-based nav-mesh baking.
/// - Provides the NavMesh agent type used when building per-chunk surfaces.
/// - Defines padding applied to chunk bounds so neighbouring meshes overlap.
/// - Controls the minimum delay between rebuilds to amortize voxel edits.
/// This asset allows design/engineering teams to tune navigation behaviour without
/// modifying code and keeps chunk/nav assumptions centralised near terrain generation.
/// </summary>
[CreateAssetMenu(menuName = "AI/Nav Mesh Chunk Settings", fileName = "NavMeshChunkSettings")]
public class NavMeshChunkSettings : ScriptableObject
{
    [Header("Agent Settings")]
    [Tooltip("NavMesh agent type used when baking chunk data. Set to match your primary AI agent.")]
    [SerializeField] private int agentTypeId = 0;

    [Header("Chunk Bounds Padding")]
    [Tooltip("Extra world-space padding applied around chunk bounds to ensure neighbouring nav-meshes overlap and avoid seams.")]
    [SerializeField] private Vector3 boundsPadding = new Vector3(1.0f, 2.0f, 1.0f);

    [Header("Rebuild Budget")]
    [Min(0.05f)]
    [Tooltip("Minimum seconds between consecutive rebuilds of the same chunk.")]
    [SerializeField] private float rebuildCooldown = 0.25f;

    [Tooltip("How many chunk nav-mesh rebuilds may run concurrently.")]
    [SerializeField] private int maxConcurrentBuilds = 2;

    /// <summary>
    /// Returns a copy of the build settings for the configured agent type. If the
    /// requested type is missing, Unity returns a struct with agentTypeID == -1; in
    /// that case we fall back to the default settings for type 0.
    /// </summary>
    public NavMeshBuildSettings BuildSettings
    {
        get
        {
            var settings = NavMesh.GetSettingsByID(agentTypeId);
            if (settings.agentTypeID == -1)
            {
                settings = NavMesh.GetSettingsByID(0);
            }
            return settings;
        }
    }

    public int AgentTypeId => agentTypeId;
    public Vector3 BoundsPadding => boundsPadding;
    public float RebuildCooldown => rebuildCooldown;
    public int MaxConcurrentBuilds => Mathf.Max(1, maxConcurrentBuilds);
}

