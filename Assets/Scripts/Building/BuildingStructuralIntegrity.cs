using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Manages structural integrity for building pieces (like Valheim's support system)
/// Pieces turn green/yellow/red based on how well supported they are
/// </summary>
public class BuildingStructuralIntegrity : MonoBehaviour
{
    [Header("Structural Settings")]
    public float maxIntegrity = 100f;
    public float foundationIntegrity = 100f; // Integrity when on ground/foundation
    public float supportDecayRate = 0.5f; // How much integrity is lost per piece away from foundation
    
    [Header("Visual Feedback")]
    public Renderer[] renderers; // Renderers to change color
    public Color wellSupportedColor = Color.green;
    public Color moderatelySupportedColor = Color.yellow;
    public Color poorlySupportedColor = Color.red;
    
    private float currentIntegrity;
    private BuildingPieceInstance pieceInstance;
    
    private void Awake()
    {
        pieceInstance = GetComponent<BuildingPieceInstance>();
        if (renderers == null || renderers.Length == 0)
        {
            renderers = GetComponentsInChildren<Renderer>();
        }
    }
    
    private void Start()
    {
        CalculateIntegrity();
        UpdateVisuals();
    }
    
    /// <summary>
    /// Calculate the structural integrity of this piece based on support
    /// </summary>
    public void CalculateIntegrity()
    {
        // Check if on ground/foundation
        if (CheckFoundationSupport())
        {
            currentIntegrity = foundationIntegrity;
            return;
        }
        
        // Check support from connected pieces
        float supportIntegrity = CalculateSupportFromPieces();
        currentIntegrity = Mathf.Clamp(supportIntegrity, 0f, maxIntegrity);
    }
    
    private bool CheckFoundationSupport()
    {
        // Raycast down to check for ground or foundation
        RaycastHit hit;
        float checkDistance = 0.5f;
        
        if (Physics.Raycast(transform.position, Vector3.down, out hit, checkDistance))
        {
            // Check if it's terrain or a foundation piece
            if (hit.collider.CompareTag("Terrain") || hit.collider.CompareTag("Foundation"))
            {
                return true;
            }
            
            // Check if it's a building piece that can support
            BuildingPieceInstance foundationPiece = hit.collider.GetComponent<BuildingPieceInstance>();
            if (foundationPiece != null && foundationPiece.buildingPiece.canSupportOthers)
            {
                BuildingStructuralIntegrity foundationIntegrity = foundationPiece.GetComponent<BuildingStructuralIntegrity>();
                if (foundationIntegrity != null && foundationIntegrity.IsWellSupported())
                {
                    return true;
                }
            }
        }
        
        return false;
    }
    
    private float CalculateSupportFromPieces()
    {
        // Find all connected building pieces
        List<BuildingPieceInstance> connectedPieces = FindConnectedPieces();
        
        if (connectedPieces.Count == 0)
        {
            return 0f; // No support
        }
        
        float bestSupport = 0f;
        
        foreach (var connectedPiece in connectedPieces)
        {
            BuildingStructuralIntegrity connectedIntegrity = connectedPiece.GetComponent<BuildingStructuralIntegrity>();
            if (connectedIntegrity == null) continue;
            
            // Get the integrity of the connected piece
            float connectedIntegrityValue = connectedIntegrity.GetCurrentIntegrity();
            
            // Apply decay based on distance
            float distance = Vector3.Distance(transform.position, connectedPiece.transform.position);
            float decay = distance * supportDecayRate;
            float supportValue = Mathf.Max(0f, connectedIntegrityValue - decay);
            
            bestSupport = Mathf.Max(bestSupport, supportValue);
        }
        
        return bestSupport;
    }
    
    private List<BuildingPieceInstance> FindConnectedPieces()
    {
        List<BuildingPieceInstance> connected = new List<BuildingPieceInstance>();
        
        // Check snap points for connections
        BuildingSnapPoint[] snapPoints = GetComponentsInChildren<BuildingSnapPoint>();
        foreach (var snapPoint in snapPoints)
        {
            if (snapPoint.isOccupied)
            {
                // Find nearby pieces
                Collider[] nearby = Physics.OverlapSphere(snapPoint.transform.position, snapPoint.snapRadius);
                foreach (var col in nearby)
                {
                    BuildingPieceInstance piece = col.GetComponent<BuildingPieceInstance>();
                    if (piece != null && piece != pieceInstance && !connected.Contains(piece))
                    {
                        connected.Add(piece);
                    }
                }
            }
        }
        
        // Also check for pieces directly below (vertical support)
        RaycastHit[] hits = Physics.RaycastAll(transform.position, Vector3.down, 2f);
        foreach (var hit in hits)
        {
            BuildingPieceInstance piece = hit.collider.GetComponent<BuildingPieceInstance>();
            if (piece != null && piece != pieceInstance && !connected.Contains(piece))
            {
                connected.Add(piece);
            }
        }
        
        return connected;
    }
    
    public float GetCurrentIntegrity()
    {
        return currentIntegrity;
    }
    
    public bool IsWellSupported()
    {
        return currentIntegrity >= maxIntegrity * 0.7f;
    }
    
    public bool IsModeratelySupported()
    {
        return currentIntegrity >= maxIntegrity * 0.3f && currentIntegrity < maxIntegrity * 0.7f;
    }
    
    public bool IsPoorlySupported()
    {
        return currentIntegrity < maxIntegrity * 0.3f;
    }
    
    /// <summary>
    /// Update visual feedback based on integrity
    /// </summary>
    public void UpdateVisuals()
    {
        Color targetColor;
        
        if (IsWellSupported())
        {
            targetColor = wellSupportedColor;
        }
        else if (IsModeratelySupported())
        {
            targetColor = moderatelySupportedColor;
        }
        else
        {
            targetColor = poorlySupportedColor;
        }
        
        // Apply color to renderers
        foreach (var renderer in renderers)
        {
            if (renderer == null) continue;
            
            foreach (var material in renderer.materials)
            {
                if (material.HasProperty("_Color"))
                {
                    material.color = targetColor;
                }
            }
        }
    }
    
    private void Update()
    {
        // Recalculate periodically (can be optimized)
        if (Time.frameCount % 30 == 0) // Every 30 frames
        {
            CalculateIntegrity();
            UpdateVisuals();
        }
    }
}




