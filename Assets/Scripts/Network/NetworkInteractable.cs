using System;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Base class for objects that can be interacted with over the network
/// </summary>
public class NetworkInteractable : NetworkBehaviour
{
    [Header("Interaction Settings")]
    [SerializeField] private float interactionRange = 3f;
    [SerializeField] private Transform interactionPoint;
    [SerializeField] private GameObject interactionPrompt;
    
    // Network variables to track state
    private NetworkVariable<bool> isInteractable = new NetworkVariable<bool>(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );
    
    private NetworkVariable<ulong> currentInteractor = new NetworkVariable<ulong>(
        ulong.MaxValue, // Invalid ID by default
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );
    
    public bool IsInteractable => isInteractable.Value;
    
    // Event called when interaction occurs
    public event Action<ulong> OnInteractionStarted;
    public event Action<ulong> OnInteractionEnded;
    
    private void Awake()
    {
        // Find interaction point if not set
        if (interactionPoint == null)
            interactionPoint = transform;
            
        // Setup prompt if available
        if (interactionPrompt != null)
            interactionPrompt.SetActive(false);
    }
    
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // Initialize state
        if (IsServer)
        {
            isInteractable.Value = true;
            currentInteractor.Value = ulong.MaxValue;
        }
    }
    
    /// <summary>
    /// Check if a player is within interaction range
    /// </summary>
    /// <param name="playerPosition">The player's position</param>
    /// <returns>True if the player is in range</returns>
    public bool IsPlayerInRange(Vector3 playerPosition)
    {
        return Vector3.Distance(interactionPoint.position, playerPosition) <= interactionRange;
    }
    
    /// <summary>
    /// Display the interaction prompt if in range
    /// </summary>
    /// <param name="show">Whether to show or hide the prompt</param>
    public void ShowInteractionPrompt(bool show)
    {
        if (interactionPrompt != null)
            interactionPrompt.SetActive(show);
    }
    
    /// <summary>
    /// Start interaction - called by the player when they press the interaction key
    /// </summary>
    public void Interact(ulong playerId)
    {
        if (!IsServer)
        {
            RequestInteractionServerRpc(playerId);
            return;
        }
        
        // Server-side interaction handling
        if (isInteractable.Value && currentInteractor.Value == ulong.MaxValue)
        {
            StartInteraction(playerId);
        }
    }
    
    /// <summary>
    /// End interaction - called when player finishes or moves away
    /// </summary>
    public void EndInteraction(ulong playerId)
    {
        if (!IsServer)
        {
            EndInteractionServerRpc(playerId);
            return;
        }
        
        // Server-side end interaction handling
        if (currentInteractor.Value == playerId)
        {
            StopInteraction();
        }
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void RequestInteractionServerRpc(ulong playerId, ServerRpcParams rpcParams = default)
    {
        // Security check - confirm the caller is who they claim to be
        if (rpcParams.Receive.SenderClientId != playerId)
        {
            Debug.LogWarning($"Player {rpcParams.Receive.SenderClientId} tried to impersonate player {playerId}");
            return;
        }
        
        // Only allow interaction if available
        if (isInteractable.Value && currentInteractor.Value == ulong.MaxValue)
        {
            StartInteraction(playerId);
        }
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void EndInteractionServerRpc(ulong playerId, ServerRpcParams rpcParams = default)
    {
        // Security check
        if (rpcParams.Receive.SenderClientId != playerId)
            return;
            
        // Only current interactor can end interaction
        if (currentInteractor.Value == playerId)
        {
            StopInteraction();
        }
    }
    
    private void StartInteraction(ulong playerId)
    {
        // Update network variables
        currentInteractor.Value = playerId;
        
        // Notify all clients about interaction
        InteractionStartedClientRpc(playerId);
        
        // Invoke event
        OnInteractionStarted?.Invoke(playerId);
    }
    
    private void StopInteraction()
    {
        // Store current interactor before clearing
        ulong interactor = currentInteractor.Value;
        
        // Clear interactor
        currentInteractor.Value = ulong.MaxValue;
        
        // Notify all clients
        InteractionEndedClientRpc(interactor);
        
        // Invoke event
        OnInteractionEnded?.Invoke(interactor);
    }
    
    [ClientRpc]
    private void InteractionStartedClientRpc(ulong playerId)
    {
        // Handle client-side visual feedback
        if (interactionPrompt != null)
            interactionPrompt.SetActive(false);
            
        // Client-side implementation to be overridden by derived classes
        OnClientInteractionStarted(playerId);
    }
    
    [ClientRpc]
    private void InteractionEndedClientRpc(ulong playerId)
    {
        // Client-side implementation to be overridden by derived classes
        OnClientInteractionEnded(playerId);
    }
    
    /// <summary>
    /// Override in derived classes to handle client-side effects
    /// </summary>
    protected virtual void OnClientInteractionStarted(ulong playerId)
    {
        // Base implementation does nothing
    }
    
    /// <summary>
    /// Override in derived classes to handle client-side effects
    /// </summary>
    protected virtual void OnClientInteractionEnded(ulong playerId)
    {
        // Base implementation does nothing
    }
    
    /// <summary>
    /// Set whether this object can be interacted with
    /// </summary>
    /// <param name="canInteract">Whether interaction is enabled</param>
    public void SetInteractable(bool canInteract)
    {
        if (IsServer)
        {
            isInteractable.Value = canInteract;
        }
        else
        {
            SetInteractableServerRpc(canInteract);
        }
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void SetInteractableServerRpc(bool canInteract)
    {
        // Only allow authorized clients to change state (implement your auth logic here)
        isInteractable.Value = canInteract;
    }
    
    // Visual debugging for the interaction range
    private void OnDrawGizmosSelected()
    {
        if (interactionPoint == null)
            interactionPoint = transform;
            
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(interactionPoint.position, interactionRange);
    }
}