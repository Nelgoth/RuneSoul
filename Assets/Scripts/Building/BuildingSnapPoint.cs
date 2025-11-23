using UnityEngine;

/// <summary>
/// Represents a snap point on a building piece for connecting to other pieces
/// </summary>
public class BuildingSnapPoint : MonoBehaviour
{
    [Header("Snap Settings")]
    public string snapType = "default"; // Type of snap point (for filtering compatible snaps)
    public float snapRadius = 0.3f; // Radius for snapping detection
    public bool isOccupied = false; // Whether this snap point is already connected
    
    [Header("Visual")]
    public bool showGizmos = true;
    public Color gizmoColor = Color.cyan;
    
    private void OnDrawGizmos()
    {
        if (!showGizmos) return;
        
        Gizmos.color = gizmoColor;
        Gizmos.DrawWireSphere(transform.position, snapRadius);
        
        // Draw direction indicator
        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(transform.position, transform.forward * 0.5f);
    }
    
    /// <summary>
    /// Check if another snap point can connect to this one
    /// </summary>
    public bool CanSnapTo(BuildingSnapPoint other, float maxDistance)
    {
        if (isOccupied || other.isOccupied) return false;
        if (snapType != other.snapType && snapType != "default" && other.snapType != "default") return false;
        
        float distance = Vector3.Distance(transform.position, other.transform.position);
        return distance <= maxDistance;
    }
    
    /// <summary>
    /// Get the rotation needed to align with another snap point
    /// </summary>
    public Quaternion GetSnapRotation(BuildingSnapPoint other)
    {
        // Calculate rotation to align forward directions
        Vector3 direction = (other.transform.position - transform.position).normalized;
        if (direction == Vector3.zero) return Quaternion.identity;
        
        return Quaternion.LookRotation(direction);
    }
}




