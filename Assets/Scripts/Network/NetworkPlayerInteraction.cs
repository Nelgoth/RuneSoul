using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

/// <summary>
/// Handles player interaction with NetworkInteractable objects
/// </summary>
[RequireComponent(typeof(NetworkPlayer))]
public class NetworkPlayerInteraction : NetworkBehaviour
{
    [Header("Interaction Settings")]
    [SerializeField] private float interactionRange = 3f;
    [SerializeField] private LayerMask interactionLayerMask;
    [SerializeField] private KeyCode interactionKey = KeyCode.E;
    [SerializeField] private Transform interactionOrigin;
    
    [Header("UI References")]
    [SerializeField] private GameObject interactionPromptUI;
    [SerializeField] private TMPro.TMP_Text interactionPromptText;
    
    // Internal state
    private NetworkInteractable currentInteractable;
    private NetworkPlayer networkPlayer;
    private bool isInteracting = false;
    
    // Cache of nearby interactables
    private List<NetworkInteractable> nearbyInteractables = new List<NetworkInteractable>();
    private float lastInteractionCheckTime = 0f;
    private float interactionCheckInterval = 0.2f; // Check every 0.2 seconds
    
    private void Awake()
    {
        networkPlayer = GetComponent<NetworkPlayer>();
        
        // Find interaction origin if not set
        if (interactionOrigin == null)
        {
            // Look for a camera or use the player's transform
            var playerCamera = GetComponentInChildren<Camera>();
            interactionOrigin = playerCamera != null ? playerCamera.transform : transform;
        }
        
        // Hide prompt initially
        if (interactionPromptUI != null)
            interactionPromptUI.SetActive(false);
    }
    
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // Only enable input and UI for the local player
        enabled = IsOwner;
        
        if (IsOwner)
        {
            Debug.Log("Network player interaction enabled for local player");
        }
    }
    
    private void Update()
    {
        if (!IsOwner) return;
        
        // Check for nearby interactables periodically
        if (Time.time - lastInteractionCheckTime > interactionCheckInterval)
        {
            FindNearbyInteractables();
            lastInteractionCheckTime = Time.time;
        }
        
        // Process interaction input
        ProcessInteractionInput();
        
        // Update UI
        UpdateInteractionUI();
    }
    
    private void FindNearbyInteractables()
    {
        nearbyInteractables.Clear();
        
        // Use OverlapSphere to find all colliders within interaction range
        Collider[] colliders = Physics.OverlapSphere(transform.position, interactionRange, interactionLayerMask);
        
        foreach (var collider in colliders)
        {
            NetworkInteractable interactable = collider.GetComponent<NetworkInteractable>();
            if (interactable != null && interactable.IsInteractable)
            {
                // Check if within range based on the interactable's own range check
                if (interactable.IsPlayerInRange(transform.position))
                {
                    nearbyInteractables.Add(interactable);
                }
            }
        }
    }
    
    private void ProcessInteractionInput()
    {
        // If we're already interacting, check if we should stop
        if (isInteracting && currentInteractable != null)
        {
            // Check if player moved out of range
            if (!currentInteractable.IsPlayerInRange(transform.position))
            {
                StopInteraction();
            }
            // Check if player pressed the key again to stop
            else if (Input.GetKeyDown(interactionKey))
            {
                StopInteraction();
            }
        }
        // Otherwise, check for new interaction
        else if (Input.GetKeyDown(interactionKey) && nearbyInteractables.Count > 0)
        {
            // Get closest interactable
            NetworkInteractable closest = GetClosestInteractable();
            if (closest != null)
            {
                StartInteraction(closest);
            }
        }
    }
    
    private NetworkInteractable GetClosestInteractable()
    {
        if (nearbyInteractables.Count == 0)
            return null;
            
        NetworkInteractable closest = nearbyInteractables[0];
        float closestDistance = Vector3.Distance(transform.position, closest.transform.position);
        
        for (int i = 1; i < nearbyInteractables.Count; i++)
        {
            float distance = Vector3.Distance(transform.position, nearbyInteractables[i].transform.position);
            if (distance < closestDistance)
            {
                closest = nearbyInteractables[i];
                closestDistance = distance;
            }
        }
        
        return closest;
    }
    
    private void StartInteraction(NetworkInteractable interactable)
    {
        currentInteractable = interactable;
        isInteracting = true;
        
        // Call the interactable's Interact method
        interactable.Interact(NetworkManager.Singleton.LocalClientId);
        
        Debug.Log($"Started interaction with {interactable.gameObject.name}");
    }
    
    private void StopInteraction()
    {
        if (currentInteractable != null)
        {
            // Call the interactable's EndInteraction method
            currentInteractable.EndInteraction(NetworkManager.Singleton.LocalClientId);
            Debug.Log($"Ended interaction with {currentInteractable.gameObject.name}");
        }
        
        currentInteractable = null;
        isInteracting = false;
    }
    
    private void UpdateInteractionUI()
    {
        if (interactionPromptUI == null) return;
        
        // If we're already interacting, hide the prompt
        if (isInteracting)
        {
            interactionPromptUI.SetActive(false);
            return;
        }
        
        // Check if we have a potential interaction
        NetworkInteractable potentialInteractable = GetClosestInteractable();
        if (potentialInteractable != null)
        {
            // Show prompt with interaction text
            interactionPromptUI.SetActive(true);
            
            if (interactionPromptText != null)
            {
                interactionPromptText.text = $"Press E to interact with {potentialInteractable.gameObject.name}";
            }
            
            // Also tell the interactable to show its prompt
            potentialInteractable.ShowInteractionPrompt(true);
        }
        else
        {
            interactionPromptUI.SetActive(false);
        }
    }
    
    private void OnDestroy()
    {
        // Ensure we stop any active interaction when destroyed
        if (isInteracting && currentInteractable != null)
        {
            StopInteraction();
        }
    }
}