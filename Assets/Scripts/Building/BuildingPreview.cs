using UnityEngine;

/// <summary>
/// Handles the preview/ghost rendering of building pieces before placement
/// Similar to Valheim's building preview system
/// </summary>
public class BuildingPreview : MonoBehaviour
{
    [Header("Preview Settings")]
    public Material previewMaterial;
    public Material validPlacementMaterial;
    public Material invalidPlacementMaterial;
    
    private GameObject previewInstance;
    private BuildingPiece currentPiece;
    private Renderer[] previewRenderers;
    private bool isValidPlacement = false;
    
    /// <summary>
    /// Show preview for a building piece
    /// </summary>
    public void ShowPreview(BuildingPiece piece, Vector3 position, Quaternion rotation)
    {
        if (piece == null || piece.prefab == null) return;
        
        // Create preview instance if needed
        if (previewInstance == null || currentPiece != piece)
        {
            DestroyPreview();
            previewInstance = Instantiate(piece.prefab, position, rotation);
            currentPiece = piece;
            
            // Make it a preview (disable colliders, set materials)
            SetupPreviewInstance();
        }
        else
        {
            previewInstance.transform.position = position;
            previewInstance.transform.rotation = rotation;
        }
        
        // Update validity
        UpdatePlacementValidity(position, rotation);
    }
    
    private void SetupPreviewInstance()
    {
        if (previewInstance == null) return;
        
        // Disable all colliders
        Collider[] colliders = previewInstance.GetComponentsInChildren<Collider>();
        foreach (var col in colliders)
        {
            col.enabled = false;
        }
        
        // Disable scripts that shouldn't run on preview
        BuildingPieceInstance pieceInstance = previewInstance.GetComponent<BuildingPieceInstance>();
        if (pieceInstance != null) pieceInstance.enabled = false;
        
        BuildingStructuralIntegrity integrity = previewInstance.GetComponent<BuildingStructuralIntegrity>();
        if (integrity != null) integrity.enabled = false;
        
        // Get all renderers
        previewRenderers = previewInstance.GetComponentsInChildren<Renderer>();
        
        // Set preview material
        ApplyPreviewMaterial();
    }
    
    private void ApplyPreviewMaterial()
    {
        if (previewRenderers == null) return;
        
        Material materialToUse = isValidPlacement ? validPlacementMaterial : invalidPlacementMaterial;
        if (materialToUse == null) materialToUse = previewMaterial;
        
        foreach (var renderer in previewRenderers)
        {
            if (renderer == null) continue;
            
            Material[] materials = new Material[renderer.materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = materialToUse;
            }
            renderer.materials = materials;
        }
    }
    
    private void UpdatePlacementValidity(Vector3 position, Quaternion rotation)
    {
        if (previewInstance == null || currentPiece == null) return;
        
        // Check if placement is valid
        BuildingPieceInstance tempInstance = previewInstance.GetComponent<BuildingPieceInstance>();
        if (tempInstance != null)
        {
            tempInstance.enabled = true; // Temporarily enable to check
            isValidPlacement = tempInstance.CanPlaceAt(position, rotation);
            tempInstance.enabled = false;
        }
        else
        {
            // Basic collision check
            Collider[] colliders = previewInstance.GetComponentsInChildren<Collider>();
            bool hasCollision = false;
            
            foreach (var col in colliders)
            {
                if (col.isTrigger) continue;
                
                Collider[] overlaps = Physics.OverlapBox(
                    col.bounds.center,
                    col.bounds.extents,
                    col.transform.rotation,
                    currentPiece.placementLayerMask
                );
                
                foreach (var overlap in overlaps)
                {
                    if (overlap.isTrigger) continue;
                    if (overlap.transform == previewInstance.transform || 
                        overlap.transform.IsChildOf(previewInstance.transform)) continue;
                    
                    hasCollision = true;
                    break;
                }
                
                if (hasCollision) break;
            }
            
            isValidPlacement = !hasCollision;
        }
        
        // Update material based on validity
        ApplyPreviewMaterial();
    }
    
    /// <summary>
    /// Hide the preview
    /// </summary>
    public void HidePreview()
    {
        DestroyPreview();
    }
    
    private void DestroyPreview()
    {
        if (previewInstance != null)
        {
            Destroy(previewInstance);
            previewInstance = null;
            currentPiece = null;
            previewRenderers = null;
        }
    }
    
    public bool IsValidPlacement()
    {
        return isValidPlacement;
    }
    
    private void OnDestroy()
    {
        DestroyPreview();
    }
}




