using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Lightweight helper that standardises NavMeshObstacle carving settings for moving props
/// (doors, crates, vehicles). Attach this to dynamic prefabs instead of configuring each
/// obstacle manually. When the object teleports more than <see cref="teleportThreshold"/>
/// metres we briefly toggle the obstacle to force Unity to recarve instantly.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshObstacle))]
public class DynamicNavObstacle : MonoBehaviour
{
    [Header("Carving")]
    [Tooltip("Automatically enable carving so the obstacle removes walkable area while present.")]
    [SerializeField] private bool enableCarving = true;

    [Tooltip("Distance in metres the obstacle may move before Unity rebuilds the carve.")]
    [Min(0.01f)]
    [SerializeField] private float carvingMoveThreshold = 0.15f;

    [Tooltip("Seconds the obstacle must remain still before carving stabilises.")]
    [Min(0f)]
    [SerializeField] private float carvingTimeToStationary = 0.1f;

    [Header("Teleport Recovery")]
    [Tooltip("If the obstacle moves farther than this in a single step we toggle it off/on to force a recarve.")]
    [Min(0.5f)]
    [SerializeField] private float teleportThreshold = 5f;

    [Tooltip("Minimum seconds between teleport checks to avoid excessive toggling.")]
    [Min(0.01f)]
    [SerializeField] private float teleportCheckInterval = 0.2f;

    private NavMeshObstacle obstacle;
    private Vector3 lastPosition;
    private Quaternion lastRotation;
    private float lastCheckTime;

    private void Awake()
    {
        obstacle = GetComponent<NavMeshObstacle>();
        ConfigureObstacle();
    }

    private void OnEnable()
    {
        CacheTransform();
    }

    private void Update()
    {
        if (!obstacle || !obstacle.carving)
        {
            return;
        }

        if (Time.time - lastCheckTime < teleportCheckInterval)
        {
            return;
        }

        float moved = Vector3.Distance(transform.position, lastPosition);
        float rotated = Quaternion.Angle(transform.rotation, lastRotation);

        if (moved >= teleportThreshold || rotated >= 90f)
        {
            // Force Unity to rebuild carve data immediately after large jumps.
            obstacle.enabled = false;
            obstacle.enabled = true;
            CacheTransform();
            return;
        }

        if (moved >= carvingMoveThreshold)
        {
            CacheTransform();
        }
    }

    private void ConfigureObstacle()
    {
        if (!obstacle)
        {
            return;
        }

        obstacle.carving = enableCarving;
        obstacle.carvingMoveThreshold = carvingMoveThreshold;
        obstacle.carvingTimeToStationary = carvingTimeToStationary;
        obstacle.carveOnlyStationary = false;
    }

    private void CacheTransform()
    {
        lastPosition = transform.position;
        lastRotation = transform.rotation;
        lastCheckTime = Time.time;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (obstacle == null)
        {
            obstacle = GetComponent<NavMeshObstacle>();
        }
        ConfigureObstacle();
    }
#endif
}

