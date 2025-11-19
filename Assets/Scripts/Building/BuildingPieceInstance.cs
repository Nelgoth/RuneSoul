using UnityEngine;
using Unity.Netcode;

/// <summary>
/// NetworkBehaviour for placed building pieces
/// Handles networking and piece-specific data
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class BuildingPieceInstance : NetworkBehaviour
{
    [Header("Piece Data")]
    public BuildingPiece buildingPiece;
    
    [Header("Snap Points")]
    public BuildingSnapPoint[] snapPoints;
    
    private BuildingStructuralIntegrity structuralIntegrity;
    
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // Initialize snap points
        if (snapPoints == null || snapPoints.Length == 0)
        {
            snapPoints = GetComponentsInChildren<BuildingSnapPoint>();
        }
        
        // Get structural integrity component
        structuralIntegrity = GetComponent<BuildingStructuralIntegrity>();
        
        // If this is a server-spawned piece, mark it as spawned
        if (IsServer)
        {
            // Initialize structural integrity
            if (structuralIntegrity != null)
            {
                structuralIntegrity.CalculateIntegrity();
            }
        }
    }
    
    /// <summary>
    /// Check if this piece can be placed at the given position
    /// </summary>
    public bool CanPlaceAt(Vector3 position, Quaternion rotation)
    {
        if (buildingPiece == null) return false;
        
        // Check placement range (should be done by BuildingSystem)
        // Check for collisions
        Collider[] colliders = GetComponentsInChildren<Collider>();
        foreach (var col in colliders)
        {
            if (col.isTrigger) continue;
            
            // Temporarily move to check collision
            Vector3 originalPos = col.transform.position;
            Quaternion originalRot = col.transform.rotation;
            
            col.transform.position = position + (col.transform.position - transform.position);
            col.transform.rotation = rotation * Quaternion.Inverse(transform.rotation) * col.transform.rotation;
            
            Collider[] overlaps = Physics.OverlapBox(
                col.bounds.center,
                col.bounds.extents,
                col.transform.rotation,
                buildingPiece.placementLayerMask
            );
            
            // Restore original position
            col.transform.position = originalPos;
            col.transform.rotation = originalRot;
            
            // Check if overlapping with non-trigger colliders
            foreach (var overlap in overlaps)
            {
                if (overlap.isTrigger) continue;
                if (overlap.transform == transform || overlap.transform.IsChildOf(transform)) continue;
                
                // Check if it's another building piece
                BuildingPieceInstance otherPiece = overlap.GetComponent<BuildingPieceInstance>();
                if (otherPiece != null && otherPiece != this)
                {
                    // Allow overlapping with other pieces if snapping
                    continue;
                }
                
                return false; // Collision detected
            }
        }
        
        return true;
    }
    
    /// <summary>
    /// Find the best snap point to connect to another piece
    /// </summary>
    public BuildingSnapPoint FindBestSnapPoint(BuildingPieceInstance otherPiece, float maxDistance)
    {
        if (snapPoints == null || snapPoints.Length == 0) return null;
        if (otherPiece.snapPoints == null || otherPiece.snapPoints.Length == 0) return null;
        
        BuildingSnapPoint bestSnap = null;
        float bestDistance = float.MaxValue;
        
        foreach (var mySnap in snapPoints)
        {
            if (mySnap.isOccupied) continue;
            
            foreach (var otherSnap in otherPiece.snapPoints)
            {
                if (otherSnap.isOccupied) continue;
                
                float distance = Vector3.Distance(mySnap.transform.position, otherSnap.transform.position);
                if (distance < bestDistance && distance <= maxDistance)
                {
                    if (mySnap.CanSnapTo(otherSnap, maxDistance))
                    {
                        bestDistance = distance;
                        bestSnap = mySnap;
                    }
                }
            }
        }
        
        return bestSnap;
    }
    
    /// <summary>
    /// Destroy this building piece (server-side)
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void DestroyPieceServerRpc()
    {
        if (!IsServer) return;
        
        // Get the NetworkObject and despawn it
        NetworkObject networkObject = GetComponent<NetworkObject>();
        if (networkObject != null && networkObject.IsSpawned)
        {
            networkObject.Despawn();
        }
        else
        {
            Destroy(gameObject);
        }
    }
    
    /// <summary>
    /// Request to destroy this piece (client calls this)
    /// </summary>
    public void RequestDestroy()
    {
        DestroyPieceServerRpc();
    }
}



