using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Maintains an off-mesh link between two Transforms. Useful for ladders, elevators, or
/// moving platforms that need runtime navigation access. Attach this to an empty GameObject
/// and assign start/end anchors; the component will rebuild the link when anchors move more
/// than <see cref="rebuildThreshold"/> metres.
/// </summary>
[DisallowMultipleComponent]
public class RuntimeNavMeshLink : MonoBehaviour
{
    [SerializeField] private Transform start;
    [SerializeField] private Transform end;
    [Tooltip("Optional nav-mesh settings asset to inherit agent type from.")]
    [SerializeField] private NavMeshChunkSettings chunkSettings;

    [Header("Link Settings")]
    [SerializeField] private float width = 1f;
    [SerializeField] private bool bidirectional = true;
    [SerializeField] private int area = 0;
    [Tooltip("Distance in metres before the link is rebuilt when anchors move.")]
    [Min(0.01f)]
    [SerializeField] private float rebuildThreshold = 0.1f;
    [Tooltip("Seconds between movement checks.")]
    [Min(0.01f)]
    [SerializeField] private float updateInterval = 0.1f;

    private NavMeshLinkInstance linkInstance;
    private Vector3 lastStartPosition;
    private Vector3 lastEndPosition;
    private float lastUpdateTime;

    private void OnEnable()
    {
        CreateOrRefreshLink(force: true);
    }

    private void OnDisable()
    {
        RemoveLink();
    }

    private void Update()
    {
        if (Time.time - lastUpdateTime < updateInterval)
        {
            return;
        }

        if (AnchorsChanged())
        {
            CreateOrRefreshLink(force: false);
        }

        lastUpdateTime = Time.time;
    }

    public void ForceRefresh()
    {
        CreateOrRefreshLink(force: true);
    }

    private void CreateOrRefreshLink(bool force)
    {
        if (start == null || end == null)
        {
            Debug.LogWarning($"[RuntimeNavMeshLink] Missing anchors on {name}");
            return;
        }

        Vector3 startPos = start.position;
        Vector3 endPos = end.position;

        if (!force && linkInstance.valid)
        {
            float startMoved = Vector3.Distance(startPos, lastStartPosition);
            float endMoved = Vector3.Distance(endPos, lastEndPosition);
            if (startMoved < rebuildThreshold && endMoved < rebuildThreshold)
            {
                return;
            }
        }

        RemoveLink();

        var linkData = new NavMeshLinkData
        {
            startPosition = startPos,
            endPosition = endPos,
            width = width,
            bidirectional = bidirectional,
            area = area,
            agentTypeID = GetAgentTypeId()
        };

        linkInstance = NavMesh.AddLink(linkData);
        linkInstance.owner = this;

        lastStartPosition = startPos;
        lastEndPosition = endPos;
        lastUpdateTime = Time.time;
    }

    private void RemoveLink()
    {
        if (linkInstance.valid)
        {
            linkInstance.Remove();
        }
        linkInstance = default;
    }

    private bool AnchorsChanged()
    {
        if (start == null || end == null)
        {
            return false;
        }

        float startMoved = Vector3.Distance(start.position, lastStartPosition);
        float endMoved = Vector3.Distance(end.position, lastEndPosition);

        return startMoved >= rebuildThreshold || endMoved >= rebuildThreshold;
    }

    private int GetAgentTypeId()
    {
        return chunkSettings != null ? chunkSettings.AgentTypeId : 0;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (isActiveAndEnabled)
        {
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null)
                {
                    return;
                }
                ForceRefresh();
            };
        }
    }
#endif
}

