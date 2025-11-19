using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

/// <summary>
/// Main building system controller - handles placement, rotation, snapping, and destruction
/// Similar to Valheim's building system
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class BuildingSystem : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private Camera playerCamera;
    [SerializeField] private BuildingPreview preview;
    [SerializeField] private BuildingMenuUI buildMenu;
    
    [Header("Building Pieces")]
    [SerializeField] private BuildingPiece[] availablePieces;
    
    [Header("Placement Settings")]
    [SerializeField] private float placementRange = 5f;
    [SerializeField] private float snapDistance = 0.5f;
    [SerializeField] private LayerMask placementLayerMask = -1;
    [SerializeField] private KeyCode buildMenuKey = KeyCode.Tab;
    [SerializeField] private KeyCode rotateKey = KeyCode.R;
    [SerializeField] private KeyCode destroyKey = KeyCode.Mouse1; // Right mouse button
    
    [Header("Destruction")]
    [SerializeField] private float destructionRange = 5f;
    [SerializeField] private GameObject destructionEffectPrefab;
    
    // Current state
    private BuildingPiece selectedPiece;
    private bool isBuildingMode = false;
    private bool isDestroyMode = false;
    private float currentRotation = 0f;
    private Vector3 previewPosition;
    private Quaternion previewRotation;
    
    // Snap tracking
    private BuildingPieceInstance snapTargetPiece;
    private BuildingSnapPoint snapTargetPoint;
    
    private void Awake()
    {
        // Find camera if not set
        if (playerCamera == null)
        {
            playerCamera = Camera.main;
        }
        
        // Create preview if not set
        if (preview == null)
        {
            GameObject previewObj = new GameObject("BuildingPreview");
            previewObj.transform.SetParent(transform);
            preview = previewObj.AddComponent<BuildingPreview>();
        }
        
        // Create build menu if not set
        if (buildMenu == null)
        {
            buildMenu = FindFirstObjectByType<BuildingMenuUI>();
            if (buildMenu == null)
            {
                Debug.LogWarning("BuildingMenuUI not found. Building menu will not be available.");
            }
        }
    }
    
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // Only enable building for the local player
        if (!IsOwner)
        {
            enabled = false;
            return;
        }
        
        // Initialize build menu
        if (buildMenu != null)
        {
            buildMenu.Initialize(this, availablePieces);
            buildMenu.OnPieceSelected += SelectPiece;
        }
    }
    
    private void Update()
    {
        if (!IsOwner) return;
        
        HandleInput();
        
        if (isBuildingMode && selectedPiece != null)
        {
            UpdatePreview();
        }
        else if (isDestroyMode)
        {
            UpdateDestroyMode();
        }
    }
    
    private void HandleInput()
    {
        // Toggle build menu
        if (Input.GetKeyDown(buildMenuKey))
        {
            ToggleBuildMenu();
        }
        
        // Exit building mode
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            ExitBuildingMode();
        }
        
        // Rotate piece
        if (isBuildingMode && Input.GetKeyDown(rotateKey))
        {
            RotatePiece();
        }
        
        // Place piece
        if (isBuildingMode && Input.GetMouseButtonDown(0))
        {
            TryPlacePiece();
        }
        
        // Toggle destroy mode
        if (Input.GetKeyDown(destroyKey))
        {
            ToggleDestroyMode();
        }
        
        // Destroy piece
        if (isDestroyMode && Input.GetMouseButtonDown(0))
        {
            TryDestroyPiece();
        }
    }
    
    private void ToggleBuildMenu()
    {
        if (buildMenu != null)
        {
            buildMenu.ToggleMenu();
        }
    }
    
    public void SelectPiece(BuildingPiece piece)
    {
        selectedPiece = piece;
        isBuildingMode = true;
        isDestroyMode = false;
        currentRotation = 0f;
        
        if (buildMenu != null)
        {
            buildMenu.HideMenu();
        }
    }
    
    private void ExitBuildingMode()
    {
        isBuildingMode = false;
        isDestroyMode = false;
        selectedPiece = null;
        preview.HidePreview();
        
        if (buildMenu != null)
        {
            buildMenu.HideMenu();
        }
    }
    
    private void ToggleDestroyMode()
    {
        isDestroyMode = !isDestroyMode;
        isBuildingMode = false;
        selectedPiece = null;
        preview.HidePreview();
    }
    
    private void RotatePiece()
    {
        if (selectedPiece == null || !selectedPiece.allowRotation) return;
        
        currentRotation += selectedPiece.rotationStep;
        if (currentRotation >= 360f)
        {
            currentRotation -= 360f;
        }
    }
    
    private void UpdatePreview()
    {
        if (selectedPiece == null || playerCamera == null) return;
        
        // Raycast from camera
        Ray ray = playerCamera.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
        RaycastHit hit;
        
        bool foundPosition = false;
        Vector3 targetPosition = Vector3.zero;
        Quaternion targetRotation = Quaternion.identity;
        
        // Check for snap points first
        if (Physics.Raycast(ray, out hit, placementRange))
        {
            BuildingPieceInstance hitPiece = hit.collider.GetComponent<BuildingPieceInstance>();
            if (hitPiece != null && selectedPiece.canSnap)
            {
                // Try to snap to this piece
                BuildingSnapPoint snapPoint = FindNearestSnapPoint(hitPiece, hit.point);
                if (snapPoint != null)
                {
                    targetPosition = snapPoint.transform.position;
                    targetRotation = snapPoint.transform.rotation * Quaternion.Euler(0, currentRotation, 0);
                    snapTargetPiece = hitPiece;
                    snapTargetPoint = snapPoint;
                    foundPosition = true;
                }
            }
            
            // If no snap, place on surface
            if (!foundPosition)
            {
                targetPosition = hit.point;
                targetRotation = Quaternion.FromToRotation(Vector3.up, hit.normal) * Quaternion.Euler(0, currentRotation, 0);
                snapTargetPiece = null;
                snapTargetPoint = null;
                foundPosition = true;
            }
        }
        
        if (foundPosition)
        {
            previewPosition = targetPosition;
            previewRotation = targetRotation;
            preview.ShowPreview(selectedPiece, previewPosition, previewRotation);
        }
        else
        {
            preview.HidePreview();
        }
    }
    
    private BuildingSnapPoint FindNearestSnapPoint(BuildingPieceInstance piece, Vector3 worldPosition)
    {
        if (piece == null || piece.snapPoints == null) return null;
        
        BuildingSnapPoint nearest = null;
        float nearestDistance = float.MaxValue;
        
        foreach (var snapPoint in piece.snapPoints)
        {
            if (snapPoint.isOccupied) continue;
            
            float distance = Vector3.Distance(worldPosition, snapPoint.transform.position);
            if (distance < nearestDistance && distance <= snapDistance)
            {
                nearestDistance = distance;
                nearest = snapPoint;
            }
        }
        
        return nearest;
    }
    
    private void TryPlacePiece()
    {
        if (selectedPiece == null || !preview.IsValidPlacement()) return;
        
        // Check if player has required resources (if implemented)
        // if (!HasRequiredResources(selectedPiece)) return;
        
        // Place the piece (server-side)
        PlacePieceServerRpc(
            selectedPiece.name,
            previewPosition,
            previewRotation,
            snapTargetPiece != null ? snapTargetPiece.NetworkObjectId : 0,
            snapTargetPoint != null ? GetSnapPointIndex(snapTargetPoint) : -1
        );
    }
    
    [ServerRpc]
    private void PlacePieceServerRpc(string pieceName, Vector3 position, Quaternion rotation, ulong snapPieceId, int snapPointIndex)
    {
        // Find the piece by name
        BuildingPiece piece = System.Array.Find(availablePieces, p => p.name == pieceName);
        if (piece == null || piece.prefab == null)
        {
            Debug.LogWarning($"Building piece '{pieceName}' not found!");
            return;
        }
        
        // Instantiate the piece
        GameObject pieceInstance = Instantiate(piece.prefab, position, rotation);
        
        // Get NetworkObject and spawn it
        NetworkObject networkObject = pieceInstance.GetComponent<NetworkObject>();
        if (networkObject == null)
        {
            networkObject = pieceInstance.AddComponent<NetworkObject>();
        }
        
        networkObject.Spawn();
        
        // Handle snapping
        if (snapPieceId != 0 && snapPointIndex >= 0)
        {
            // Safely get the NetworkObject
            if (NetworkManager.Singleton != null && 
                NetworkManager.Singleton.SpawnManager != null &&
                NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(snapPieceId, out NetworkObject snapPieceObj))
            {
                if (snapPieceObj != null)
                {
                    BuildingPieceInstance snapPiece = snapPieceObj.GetComponent<BuildingPieceInstance>();
                    if (snapPiece != null && snapPiece.snapPoints != null && snapPointIndex < snapPiece.snapPoints.Length)
                    {
                        BuildingSnapPoint snapPoint = snapPiece.snapPoints[snapPointIndex];
                        snapPoint.isOccupied = true;
                        
                        // Find corresponding snap point on new piece
                        BuildingPieceInstance newPiece = pieceInstance.GetComponent<BuildingPieceInstance>();
                        if (newPiece != null && newPiece.snapPoints != null)
                        {
                            BuildingSnapPoint newSnapPoint = FindClosestSnapPoint(newPiece, snapPoint.transform.position);
                            if (newSnapPoint != null)
                            {
                                newSnapPoint.isOccupied = true;
                            }
                        }
                    }
                }
            }
        }
        
        // Initialize structural integrity
        BuildingPieceInstance pieceInstanceComponent = pieceInstance.GetComponent<BuildingPieceInstance>();
        if (pieceInstanceComponent != null)
        {
            BuildingStructuralIntegrity integrity = pieceInstanceComponent.GetComponent<BuildingStructuralIntegrity>();
            if (integrity != null)
            {
                integrity.CalculateIntegrity();
            }
        }
    }
    
    private BuildingSnapPoint FindClosestSnapPoint(BuildingPieceInstance piece, Vector3 position)
    {
        if (piece == null || piece.snapPoints == null) return null;
        
        BuildingSnapPoint closest = null;
        float closestDistance = float.MaxValue;
        
        foreach (var snapPoint in piece.snapPoints)
        {
            float distance = Vector3.Distance(position, snapPoint.transform.position);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = snapPoint;
            }
        }
        
        return closest;
    }
    
    private int GetSnapPointIndex(BuildingSnapPoint snapPoint)
    {
        if (snapTargetPiece == null || snapTargetPiece.snapPoints == null) return -1;
        
        for (int i = 0; i < snapTargetPiece.snapPoints.Length; i++)
        {
            if (snapTargetPiece.snapPoints[i] == snapPoint)
            {
                return i;
            }
        }
        
        return -1;
    }
    
    private void UpdateDestroyMode()
    {
        if (playerCamera == null) return;
        
        // Raycast to find building pieces
        Ray ray = playerCamera.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
        RaycastHit hit;
        
        if (Physics.Raycast(ray, out hit, destructionRange))
        {
            BuildingPieceInstance piece = hit.collider.GetComponent<BuildingPieceInstance>();
            if (piece != null)
            {
                // Highlight piece (could add visual feedback here)
                // For now, just show in console
            }
        }
    }
    
    private void TryDestroyPiece()
    {
        if (playerCamera == null) return;
        
        // Raycast to find building pieces
        Ray ray = playerCamera.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
        RaycastHit hit;
        
        if (Physics.Raycast(ray, out hit, destructionRange))
        {
            BuildingPieceInstance piece = hit.collider.GetComponent<BuildingPieceInstance>();
            if (piece != null)
            {
                // Request destruction (server-side)
                piece.RequestDestroy();
                
                // Spawn destruction effect
                if (destructionEffectPrefab != null)
                {
                    Instantiate(destructionEffectPrefab, hit.point, Quaternion.identity);
                }
            }
        }
    }
    
    public override void OnDestroy()
    {
        preview.HidePreview();
        base.OnDestroy();
    }
}

