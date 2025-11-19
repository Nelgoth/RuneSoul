using UnityEngine;

/// <summary>
/// ScriptableObject that defines a building piece (wall, floor, roof, etc.)
/// Similar to Valheim's building pieces
/// </summary>
[CreateAssetMenu(fileName = "New Building Piece", menuName = "Building/Building Piece")]
public class BuildingPiece : ScriptableObject
{
    [Header("Basic Info")]
    public string pieceName;
    [TextArea(3, 5)]
    public string description;
    public Sprite icon;
    
    [Header("Prefab")]
    public GameObject prefab; // The actual prefab to instantiate
    
    [Header("Snapping")]
    public bool canSnap = true;
    public float snapDistance = 0.5f; // Distance to snap to other pieces
    public Vector3[] snapPoints; // Local positions of snap points
    
    [Header("Structural")]
    public float structuralIntegrity = 100f; // Base structural integrity
    public bool requiresFoundation = false; // Must be placed on ground or foundation
    public bool canSupportOthers = true; // Can support other pieces above it
    
    [Header("Placement")]
    public float placementRange = 5f; // Max distance from player to place
    public LayerMask placementLayerMask = -1; // What layers can be placed on
    public bool canPlaceOnGround = true;
    public bool canPlaceOnPieces = true;
    
    [Header("Rotation")]
    public bool allowRotation = true;
    public float rotationStep = 90f; // Degrees per rotation step
    
    [Header("Materials")]
    public Material previewMaterial; // Material for preview (usually transparent)
    public Material validPlacementMaterial; // Green material
    public Material invalidPlacementMaterial; // Red material
    
    [Header("Cost")]
    public BuildingResourceCost[] resourceCosts; // Resources needed to build
    
    [System.Serializable]
    public class BuildingResourceCost
    {
        public string resourceName;
        public int quantity;
    }
}



