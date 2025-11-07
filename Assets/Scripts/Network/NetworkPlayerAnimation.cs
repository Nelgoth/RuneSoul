using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;

[RequireComponent(typeof(NetworkAnimator))]
public class NetworkPlayerAnimation : NetworkBehaviour
{
    private Animator animator;
    private NetworkAnimator networkAnimator;
    
    // Common animation parameter hashes
    private int speedHash;
    private int motionSpeedHash;
    private int groundedHash;
    private int jumpHash;
    private int freeFallHash;
    
    private void Awake()
    {
        animator = GetComponent<Animator>();
        networkAnimator = GetComponent<NetworkAnimator>();
        
        // Cache animation parameter hashes
        speedHash = Animator.StringToHash("Speed");
        motionSpeedHash = Animator.StringToHash("MotionSpeed");
        groundedHash = Animator.StringToHash("Grounded");
        jumpHash = Animator.StringToHash("Jump");
        freeFallHash = Animator.StringToHash("FreeFall");
    }
    
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        if (networkAnimator == null || animator == null)
        {
            Debug.LogError($"Missing required components on {gameObject.name}");
            return;
        }
        
        // Force animator setup
        networkAnimator.Animator = animator;
        
        // Manually add parameters to sync (to be sure)
        if (IsServer)
        {
            Debug.Log($"Setting up NetworkAnimator for {gameObject.name}, ClientId: {OwnerClientId}");
        }
        
        // Force synchronization of parameters
        InvokeRepeating(nameof(ForceSyncAnimationState), 0.1f, 0.1f);
    }
    
    private void ForceSyncAnimationState()
    {
        if (!IsSpawned) return;
        
        // Only the client owner will force sync their animations to the server
        if (IsOwner && !IsServer)
        {
            // Trigger a sync by toggling a parameter
            // This is a hack that forces NetworkAnimator to sync
            bool currentGrounded = animator.GetBool(groundedHash);
            
            SyncAnimationServerRpc(
                animator.GetFloat(speedHash),
                animator.GetFloat(motionSpeedHash),
                animator.GetBool(groundedHash),
                animator.GetBool(jumpHash),
                animator.GetBool(freeFallHash)
            );
        }
    }
    
    [ServerRpc]
    private void SyncAnimationServerRpc(float speed, float motionSpeed, bool grounded, bool jump, bool freeFall)
    {
        // On the server, make sure these values are set
        if (!IsServer) return;
        
        animator.SetFloat(speedHash, speed);
        animator.SetFloat(motionSpeedHash, motionSpeed);
        animator.SetBool(groundedHash, grounded);
        animator.SetBool(jumpHash, jump);
        animator.SetBool(freeFallHash, freeFall);
        
        // Then sync to all clients
        SyncAnimationClientRpc(speed, motionSpeed, grounded, jump, freeFall);
    }
    
    [ClientRpc]
    private void SyncAnimationClientRpc(float speed, float motionSpeed, bool grounded, bool jump, bool freeFall)
    {
        // Skip if we're the owner - we already have these values
        if (IsOwner) return;
        
        animator.SetFloat(speedHash, speed);
        animator.SetFloat(motionSpeedHash, motionSpeed);
        animator.SetBool(groundedHash, grounded);
        animator.SetBool(jumpHash, jump);
        animator.SetBool(freeFallHash, freeFall);
    }
    
    public override void OnNetworkDespawn()
    {
        CancelInvoke(nameof(ForceSyncAnimationState));
        base.OnNetworkDespawn();
    }
}