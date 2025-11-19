using UnityEngine;
using Unity.Netcode;
using ControllerAssets;

[RequireComponent(typeof(NetworkObject))]
public class NetworkPlayer : NetworkBehaviour
{
    [Header("Player Components")]
    [SerializeField] private ThirdPersonController controller;
    [SerializeField] private GameObject visualsContainer; // Character model and visuals
    
    // NetworkVariables for position and rotation syncing
    private NetworkVariable<Vector3> networkPosition = new NetworkVariable<Vector3>(
        Vector3.zero,
        NetworkVariableReadPermission.Everyone, 
        NetworkVariableWritePermission.Server  // Change from Owner to Server
    );
    
    private NetworkVariable<Quaternion> networkRotation = new NetworkVariable<Quaternion>(
        Quaternion.identity,
        NetworkVariableReadPermission.Everyone, 
        NetworkVariableWritePermission.Server  // Change from Owner to Server
    );
    
    // NetworkVariable for player color
    private NetworkVariable<Vector3> networkColor = new NetworkVariable<Vector3>(
        new Vector3(1, 1, 1), // Default to white
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // For client-side prediction and smooth movement
    private Vector3 targetPosition;
    private Quaternion targetRotation;
    private float movementSmoothTime = 0.1f;
    private Vector3 positionVelocity;

    private void Awake()
    {
        // Find references if not set
        if (controller == null)
            controller = GetComponent<ThirdPersonController>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        Debug.Log($"Player {OwnerClientId} network object spawned as {(IsOwner ? "owner" : "non-owner")}");
        
        // Setup per owner
        if (IsOwner)
        {
            Debug.Log($"Player {OwnerClientId} network object spawned as owner");
            
            // Enable input control for the owner
            if (controller != null)
                controller.enabled = true;
                
            // Only the server should set network variables
            if (IsServer)
            {
                networkPosition.Value = transform.position;
                networkRotation.Value = transform.rotation;
            }
            else
            {
                // Client owner should send initial position to server
                UpdatePositionServerRpc(transform.position, transform.rotation);
            }
        }
        else
        {
            Debug.Log($"Player {OwnerClientId} network object spawned as non-owner");
            
            // Disable input control for non-owners
            if (controller != null)
                controller.enabled = false;
            
            // Set initial position from network
            transform.position = networkPosition.Value;
            transform.rotation = networkRotation.Value;
            
            targetPosition = networkPosition.Value;
            targetRotation = networkRotation.Value;
        }
    }

    private void Update()
    {
        if (IsOwner)
        {
            // Send position to server using ServerRpc
            UpdatePositionServerRpc(transform.position, transform.rotation);
        }
        else
        {
            // Non-owners update position from network
            SmoothlyUpdateTransform();
        }
    }

    [ServerRpc]
    private void UpdatePositionServerRpc(Vector3 position, Quaternion rotation)
    {
        // Only the server should update the NetworkVariables
        if (!IsServer)
            return;

        // Optional validation
        float maxAllowedDistance = 10f;
        if (Vector3.Distance(networkPosition.Value, position) > maxAllowedDistance)
        {
            Debug.LogWarning($"Player {OwnerClientId} attempted to move too far in a single update");
            // You could handle this differently - teleport back, etc.
        }

        // Update the NetworkVariables
        networkPosition.Value = position;
        networkRotation.Value = rotation;
    }

    // Called only on non-owner clients to smooth character movement
    private void SmoothlyUpdateTransform()
    {
        // Update target position and rotation from network values
        targetPosition = networkPosition.Value;
        targetRotation = networkRotation.Value;
        
        // Smoothly move to the target position
        transform.position = Vector3.SmoothDamp(
            transform.position, 
            targetPosition, 
            ref positionVelocity, 
            movementSmoothTime
        );
        
        // Smoothly rotate to target rotation
        transform.rotation = Quaternion.Slerp(
            transform.rotation, 
            targetRotation, 
            Time.deltaTime * 10f
        );
        
        // Apply color if it changed
        ApplyPlayerColor();
    }
    
    private void ApplyPlayerColor()
    {
        Color playerColor = new Color(
            networkColor.Value.x,
            networkColor.Value.y,
            networkColor.Value.z
        );
        
        // Apply to all renderers tagged as PlayerModel
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        foreach (var renderer in renderers)
        {
            if (renderer.gameObject.CompareTag("PlayerModel"))
            {
                foreach (var material in renderer.materials)
                {
                    if (material.HasProperty("_Color"))
                    {
                        material.color = playerColor;
                    }
                }
            }
        }
    }

    [ServerRpc(RequireOwnership = true)] // This forces ownership validation
    private void UpdateNetworkPositionServerRpc(Vector3 position, Quaternion rotation)
    {
        // Since we've set RequireOwnership = true, this check is redundant but adds clarity
        if (!IsOwner && !IsServer)
        {
            Debug.LogWarning($"Non-owner tried to update position for player {OwnerClientId}");
            return;
        }
        
        // The rest of your existing method...
        float maxAllowedDistance = 10f;
        
        if (Vector3.Distance(networkPosition.Value, position) <= maxAllowedDistance)
        {
            networkPosition.Value = position;
            networkRotation.Value = rotation;
        }
        else
        {
            Debug.LogWarning($"Player {OwnerClientId} attempted to move too far in a single update");
            networkPosition.Value = position;
            networkRotation.Value = rotation;
        }
    }

    [ClientRpc]
    private void TeleportClientRpc(Vector3 position)
    {
        // Force position on the client (including owner)
        if (IsOwner)
        {
            transform.position = position;
            
            // If using CharacterController, need to update it too
            if (controller != null && controller.TryGetComponent<CharacterController>(out var charController))
            {
                charController.enabled = false;
                transform.position = position;
                charController.enabled = true;
            }
        }
    }
    
    [ServerRpc(RequireOwnership = false)]
    public void SetPlayerColorServerRpc(Vector3 colorRGB)
    {
        // Validate color values (ensure they're between 0-1)
        colorRGB.x = Mathf.Clamp01(colorRGB.x);
        colorRGB.y = Mathf.Clamp01(colorRGB.y);
        colorRGB.z = Mathf.Clamp01(colorRGB.z);
        
        // Set the network color
        networkColor.Value = colorRGB;
        
        // Apply immediately on the server
        ApplyPlayerColor();
    }
}