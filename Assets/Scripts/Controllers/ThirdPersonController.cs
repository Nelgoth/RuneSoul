// REPLACE ENTIRE FILE
using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ControllerAssets
{
    [RequireComponent(typeof(CharacterController))]
#if ENABLE_INPUT_SYSTEM
    [RequireComponent(typeof(PlayerInput))]
#endif
    public class ThirdPersonController : NetworkBehaviour
    {
        [Header("Player")]
        [Tooltip("Move speed of the character in m/s")]
        public float MoveSpeed = 2.0f;

        [Tooltip("Sprint speed of the character in m/s")]
        public float SprintSpeed = 5.335f;

        [Tooltip("How fast the character turns to face movement direction")]
        [Range(0.0f, 0.3f)]
        public float RotationSmoothTime = 0.12f;

        [Tooltip("Acceleration and deceleration")]
        public float SpeedChangeRate = 10.0f;

        public AudioClip LandingAudioClip;
        public AudioClip[] FootstepAudioClips;
        [Range(0, 1)] public float FootstepAudioVolume = 0.5f;

        [Space(10)]
        [Tooltip("The height the player can jump")]
        public float JumpHeight = 1.2f;

        [Tooltip("The character uses its own gravity value. The engine default is -9.81f")]
        public float Gravity = -15.0f;

        [Space(10)]
        [Tooltip("Time required to pass before being able to jump again. Set to 0f to instantly jump again")]
        public float JumpTimeout = 0.50f;

        [Tooltip("Time required to pass before entering the fall state. Useful for walking down stairs")]
        public float FallTimeout = 0.15f;

        [Header("Player Grounded")]
        [Tooltip("If the character is grounded or not. Not part of the CharacterController built in grounded check")]
        public bool Grounded = true;

        [Tooltip("Useful for rough ground")]
        public float GroundedOffset = -0.14f;

        [Tooltip("The radius of the grounded check. Should match the radius of the CharacterController")]
        public float GroundedRadius = 0.28f;

        [Tooltip("What layers the character uses as ground")]
        public LayerMask GroundLayers;

        [Header("Cinemachine")]
        [Tooltip("The follow target set in the Cinemachine Virtual Camera that the camera will follow")]
        public GameObject CinemachineCameraTarget;

        [Tooltip("How far in degrees can you move the camera up")]
        public float TopClamp = 70.0f;

        [Tooltip("How far in degrees can you move the camera down")]
        public float BottomClamp = -30.0f;

        [Tooltip("Additional degress to override the camera. Useful for fine tuning camera position when locked")]
        public float CameraAngleOverride = 0.0f;

        [Tooltip("For locking the camera position on all axis")]
        public bool LockCameraPosition = false;

        // Terrain Modification
        public bool isInitialized = false;
        public GameObject hitBox;
        public Transform cameraTransform;
        private int terrainLayerMask;

        // cinemachine
        private float _cinemachineTargetYaw;
        private float _cinemachineTargetPitch;

        // player
        private float _speed;
        private float _animationBlend;
        private float _targetRotation = 0.0f;
        private float _rotationVelocity;
        private float _verticalVelocity;
        private float _terminalVelocity = 53.0f;

        // timeout deltatime
        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;

        // animation IDs
        private int _animIDSpeed;
        private int _animIDGrounded;
        private int _animIDJump;
        private int _animIDFreeFall;
        private int _animIDMotionSpeed;

        private bool hasCameraAssigned = false;
        private bool hasInitialized = false;
#if ENABLE_INPUT_SYSTEM
        private PlayerInput _playerInput;
#endif
        private Animator _animator;
        private CharacterController _controller;
        private ControllerInputs _input;
        private GameObject _mainCamera;

        private const float _threshold = 0.01f;

        private bool _hasAnimator;

        private bool IsCurrentDeviceMouse
        {
            get
            {
        #if ENABLE_INPUT_SYSTEM
                // Add null check before accessing currentControlScheme
                if (_playerInput == null)
                {
                    _playerInput = GetComponent<PlayerInput>();
                    
                    // If we still don't have it, default to true (assume mouse)
                    if (_playerInput == null)
                        return true;
                }
                
                return _playerInput.currentControlScheme == "KeyboardMouse";
        #else
                return false;
        #endif
            }
        }

        private void Awake()
        {
            // get a reference to our main camera
            if (_mainCamera == null)
            {
                _mainCamera = GameObject.FindGameObjectWithTag("MainCamera");
            }
            terrainLayerMask = LayerMask.GetMask("Terrain");
        }

        private void Start()
        {
            // Create a CinemachineCameraTarget if it doesn't exist
            if (CinemachineCameraTarget == null)
            {
                // Create a new GameObject as a child
                GameObject targetObj = new GameObject("CinemachineCameraTarget");
                targetObj.transform.SetParent(transform, false);
                
                // Position it at eye level
                targetObj.transform.localPosition = new Vector3(0, 1.6f, 0); // Adjust height as needed
                
                // Assign it
                CinemachineCameraTarget = targetObj;
                Debug.Log("Created missing CinemachineCameraTarget");
            }

            // Now safely access the target
            _cinemachineTargetYaw = CinemachineCameraTarget.transform.rotation.eulerAngles.y;
            
            _hasAnimator = TryGetComponent(out _animator);
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<ControllerInputs>();

            #if ENABLE_INPUT_SYSTEM
                _playerInput = GetComponent<PlayerInput>();
                if (_playerInput == null)
                {
                    Debug.LogWarning("PlayerInput component not found for ThirdPersonController. Some functionality may be limited.");
                }
            #endif
            AssignAnimationIDs();

            // reset our timeouts on start
            _jumpTimeoutDelta = JumpTimeout;
            _fallTimeoutDelta = FallTimeout;
            
            hasInitialized = true;
        }

        private void Update()
        {
            // Skip processing if not fully initialized
            if (!hasInitialized) return;
            if (!IsOwner) return;
            
            // Validate all references before proceeding
            if (!ValidateReferences()) return;
            
            // Make sure we have required components
            if (_input == null)
            {
                _input = GetComponent<ControllerInputs>();
                if (_input == null) return; // Skip this frame if still null
            }
            
            _hasAnimator = TryGetComponent(out _animator);

            JumpAndGravity();
            GroundedCheck();
            Move();
            PrimaryAction();
        }

        private void LateUpdate()
        {
            // Skip processing if not fully initialized
            if (!hasInitialized) return;
            
            // Skip camera rotation if required objects are missing
            if (CinemachineCameraTarget == null || _input == null || transform == null) return;
            
            CameraRotation();
        }

        public ControllerInputs GetInputComponent()
        {
            if (_input == null)
            {
                _input = GetComponent<ControllerInputs>();
            }
            return _input;
        }

        // Public method to reset input state
        public void ResetInputState()
        {
            if (_input != null)
            {
                // Reset commonly used flags
                _input.jump = false;
                _input.sprint = false;
                _input.primaryAction = false;
                _input.move = Vector2.zero;
                _input.look = Vector2.zero;
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            ValidateNetworkAnimator();
            // Wait a frame to make sure everything is set up
            StartCoroutine(DelayedCameraSetup());
        }
        
        private void ValidateNetworkAnimator()
        {
            var networkAnimator = GetComponent<NetworkAnimator>();
            if (networkAnimator == null)
            {
                Debug.LogError($"NetworkAnimator missing on player {gameObject.name}");
                return;
            }

            var animator = GetComponent<Animator>();
            if (animator == null)
            {
                Debug.LogError($"Animator missing on player {gameObject.name}");
                return;
            }

            if (networkAnimator.Animator != animator)
            {
                Debug.LogError($"NetworkAnimator not properly referenced to Animator on {gameObject.name}");
                return;
            }

            var netObj = GetComponent<NetworkObject>();
            if (netObj == null || !netObj.IsSpawned)
            {
                Debug.LogError($"NetworkObject not found or not spawned on {gameObject.name}");
                return;
            }

            Debug.Log($"Network Animator setup validated for {gameObject.name}. IsOwner: {IsOwner}, ClientId: {netObj.OwnerClientId}");
        }
        private IEnumerator DelayedCameraSetup()
        {
            // Wait a frame to ensure cameras are properly set up
            yield return null;
            
            // Find and assign camera
            FindAndAssignCamera();
        }

        private void AssignAnimationIDs()
        {
            _animIDSpeed = Animator.StringToHash("Speed");
            _animIDGrounded = Animator.StringToHash("Grounded");
            _animIDJump = Animator.StringToHash("Jump");
            _animIDFreeFall = Animator.StringToHash("FreeFall");
            _animIDMotionSpeed = Animator.StringToHash("MotionSpeed");
        }

        private void GroundedCheck()
        {
            // set sphere position, with offset
            Vector3 spherePosition = new Vector3(transform.position.x, transform.position.y - GroundedOffset,
                transform.position.z);
            Grounded = Physics.CheckSphere(spherePosition, GroundedRadius, GroundLayers,
                QueryTriggerInteraction.Ignore);

            // update animator if using character
            if (_hasAnimator)
            {
                _animator.SetBool(_animIDGrounded, Grounded);
            }
        }

        private void FindAndAssignCamera()
        {
            if (cameraTransform != null)
                return;

            // Use our new utility to find the best camera
            Transform bestCamera = gameObject.GetBestCameraTransform();
            
            if (bestCamera != null)
            {
                cameraTransform = bestCamera;
                Debug.Log($"Found camera for ThirdPersonController: {cameraTransform.name}");
                return;
            }
            
            // Legacy camera finding logic as a fallback
            Camera mainCamera = Camera.main;
            if (mainCamera != null)
            {
                cameraTransform = mainCamera.transform;
                Debug.Log($"Using main camera at {cameraTransform.position}");
                return;
            }
            
            // Player camera components as last resort
            Camera playerCamera = GetComponentInChildren<Camera>();
            if (playerCamera != null)
            {
                cameraTransform = playerCamera.transform;
                Debug.Log($"Using player's own camera at {cameraTransform.position}");
                return;
            }
            
            Debug.LogWarning("No camera found in the scene! Player actions requiring camera will not work.");
        }

        private bool ValidateReferences()
    {
        // Check for destroyed objects
        if (this == null || gameObject == null || !gameObject.activeInHierarchy)
        {
            return false;
        }
        
        // Check for missing components
        if (_input == null)
        {
            _input = GetComponent<ControllerInputs>();
            if (_input == null)
            {
                return false;
            }
        }
        
        // Make sure the camera reference is valid
        if (cameraTransform == null && _mainCamera != null)
        {
            cameraTransform = _mainCamera.transform;
        }
        
        return true;
    }

        private void CameraRotation()
        {
            // Extra safety check at the start of the method
            if (_input == null || CinemachineCameraTarget == null) return;
            
            // if there is an input and camera position is not fixed
            if (_input.look.sqrMagnitude >= _threshold && !LockCameraPosition)
            {
                //Don't multiply mouse input by Time.deltaTime;
                float deltaTimeMultiplier = IsCurrentDeviceMouse ? 1.0f : Time.deltaTime;

                _cinemachineTargetYaw += _input.look.x * deltaTimeMultiplier;
                _cinemachineTargetPitch += _input.look.y * deltaTimeMultiplier;
            }

            // clamp our rotations so our values are limited 360 degrees
            _cinemachineTargetYaw = ClampAngle(_cinemachineTargetYaw, float.MinValue, float.MaxValue);
            _cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, BottomClamp, TopClamp);

            // Cinemachine will follow this target
            CinemachineCameraTarget.transform.rotation = Quaternion.Euler(_cinemachineTargetPitch + CameraAngleOverride,
                _cinemachineTargetYaw, 0.0f);
        }

        private void Move()
        {
            // Extra safety checks at the start
            if (_input == null || transform == null || _controller == null)
            {
                return;
            }
            
            // Safety check for mainCamera reference
            if (_mainCamera == null)
            {
                _mainCamera = GameObject.FindGameObjectWithTag("MainCamera");
                if (_mainCamera == null)
                {
                    // If still no camera, skip movement processing
                    return;
                }
            }

            // set target speed based on move speed, sprint speed and if sprint is pressed
            float targetSpeed = _input.sprint ? SprintSpeed : MoveSpeed;

            // a simplistic acceleration and deceleration designed to be easy to remove, replace, or iterate upon

            // note: Vector2's == operator uses approximation so is not floating point error prone, and is cheaper than magnitude
            // if there is no input, set the target speed to 0
            if (_input.move == Vector2.zero) targetSpeed = 0.0f;

            // a reference to the players current horizontal velocity
            float currentHorizontalSpeed = new Vector3(_controller.velocity.x, 0.0f, _controller.velocity.z).magnitude;

            float speedOffset = 0.1f;
            float inputMagnitude = _input.analogMovement ? _input.move.magnitude : 1f;

            // accelerate or decelerate to target speed
            if (currentHorizontalSpeed < targetSpeed - speedOffset ||
                currentHorizontalSpeed > targetSpeed + speedOffset)
            {
                // creates curved result rather than a linear one giving a more organic speed change
                // note T in Lerp is clamped, so we don't need to clamp our speed
                _speed = Mathf.Lerp(currentHorizontalSpeed, targetSpeed * inputMagnitude,
                    Time.deltaTime * SpeedChangeRate);

                // round speed to 3 decimal places
                _speed = Mathf.Round(_speed * 1000f) / 1000f;
            }
            else
            {
                _speed = targetSpeed;
            }

            _animationBlend = Mathf.Lerp(_animationBlend, targetSpeed, Time.deltaTime * SpeedChangeRate);
            if (_animationBlend < 0.01f) _animationBlend = 0f;

            // normalise input direction
            Vector3 inputDirection = new Vector3(_input.move.x, 0.0f, _input.move.y).normalized;

            // note: Vector2's != operator uses approximation so is not floating point error prone, and is cheaper than magnitude
            // if there is a move input rotate player when the player is moving
            if (_input.move != Vector2.zero)
            {
                _targetRotation = Mathf.Atan2(inputDirection.x, inputDirection.z) * Mathf.Rad2Deg +
                                _mainCamera.transform.eulerAngles.y;
                float rotation = Mathf.SmoothDampAngle(transform.eulerAngles.y, _targetRotation, ref _rotationVelocity,
                    RotationSmoothTime);

                // rotate to face input direction relative to camera position
                transform.rotation = Quaternion.Euler(0.0f, rotation, 0.0f);
            }

            Vector3 targetDirection = Quaternion.Euler(0.0f, _targetRotation, 0.0f) * Vector3.forward;

            // move the player
            Vector3 movement = targetDirection.normalized * (_speed * Time.deltaTime) +
                new Vector3(0.0f, _verticalVelocity, 0.0f) * Time.deltaTime;

            // Move the player once
            _controller.Move(movement);

            // Update animator parameters once (only on the server)
            if (_hasAnimator)
            {
                _animator.SetFloat(_animIDSpeed, _animationBlend);
                _animator.SetFloat(_animIDMotionSpeed, inputMagnitude);
            }

            // Add this after player movement
            if (World.Instance != null)
            {
                World.Instance.UpdatePlayerPosition(transform.position);
            }
        }

        private void JumpAndGravity()
        {
            if (Grounded)
            {
                // reset the fall timeout timer
                _fallTimeoutDelta = FallTimeout;

                // update animator if using character
                if (_hasAnimator)
                {
                    _animator.SetBool(_animIDJump, false);
                    _animator.SetBool(_animIDFreeFall, false);
                }

                // stop our velocity dropping infinitely when grounded
                if (_verticalVelocity < 0.0f)
                {
                    _verticalVelocity = -2f;
                }

                // Jump
                if (_input.jump && _jumpTimeoutDelta <= 0.0f)
                {
                    // the square root of H * -2 * G = how much velocity needed to reach desired height
                    _verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);

                    // update animator if using character
                    if (_hasAnimator)
                    {
                        _animator.SetBool(_animIDJump, true);
                    }
                }

                // jump timeout
                if (_jumpTimeoutDelta >= 0.0f)
                {
                    _jumpTimeoutDelta -= Time.deltaTime;
                }
            }
            else
            {
                // reset the jump timeout timer
                _jumpTimeoutDelta = JumpTimeout;

                // fall timeout
                if (_fallTimeoutDelta >= 0.0f)
                {
                    _fallTimeoutDelta -= Time.deltaTime;
                }
                else
                {
                    // update animator if using character
                    if (_hasAnimator)
                    {
                        _animator.SetBool(_animIDFreeFall, true);
                    }
                }

                // if we are not grounded, do not jump
                _input.jump = false;
            }

            // apply gravity over time if under terminal (multiply by delta time twice to linearly speed up over time)
            if (_verticalVelocity < _terminalVelocity)
            {
                _verticalVelocity += Gravity * Time.deltaTime;
            }
        }

        private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
        {
            if (lfAngle < -360f) lfAngle += 360f;
            if (lfAngle > 360f) lfAngle -= 360f;
            return Mathf.Clamp(lfAngle, lfMin, lfMax);
        }

        private void OnDrawGizmosSelected()
        {
            Color transparentGreen = new Color(0.0f, 1.0f, 0.0f, 0.35f);
            Color transparentRed = new Color(1.0f, 0.0f, 0.0f, 0.35f);

            if (Grounded) Gizmos.color = transparentGreen;
            else Gizmos.color = transparentRed;

            // when selected, draw a gizmo in the position of, and matching radius of, the grounded collider
            Gizmos.DrawSphere(
                new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z),
                GroundedRadius);
        }

        // Methods for World integration
        public Vector3 getPlayerPosition()
        {
            return transform.position;
        }

        public void PrimaryAction()
        {
            // Make sure camera is assigned
            if (cameraTransform == null)
            {
                FindAndAssignCamera();
                if (cameraTransform == null)
                {
                    Debug.LogWarning("Cannot perform primary action - still no camera assigned after search attempt");
                    _input.primaryAction = false; // Reset input state to prevent spam warnings
                    return;
                }
            }
            
            if (_input.primaryAction)
            {
                Debug.Log("Primary action triggered with camera: " + cameraTransform.name);
                RemoveTerrain();
                // Reset the input state after processing
                _input.primaryAction = false;
            }
        }
        
        public void RemoveTerrain()
        {
            Debug.Log("Remove Terrain Triggered");
            // Make sure we have the required components
            if (hitBox == null)
            {
                Debug.LogWarning("HitBox reference is missing. Can't remove terrain.");
                return;
            }

            if (cameraTransform == null)
            {
                // Try to find main camera if not assigned
                var mainCamera = Camera.main;
                if (mainCamera != null)
                {
                    cameraTransform = mainCamera.transform;
                }
                else
                {
                    Debug.LogWarning("Camera transform is missing. Can't remove terrain.");
                    return;
                }
            }
            Ray ray = new Ray(hitBox.transform.position, cameraTransform.forward);
            RaycastHit hit;

            // Draw the ray in the Scene view for visualization
            Debug.DrawRay(hitBox.transform.position, cameraTransform.forward * 10, Color.red, 1f);

            if (Physics.Raycast(ray, out hit, 100f, terrainLayerMask))
            {
                Vector3 hitPoint = hit.point;
                Vector3 normal = hit.normal;
                bool isAdding = false; // Since we're removing terrain

                // Log the hit information
                Debug.Log($"Raycast Hit at: {hitPoint}, Normal: {normal}");

                Vector3Int voxelPosition = World.Instance.GetVoxelPositionFromHit(hitPoint, normal, isAdding);
                if (voxelPosition != Vector3Int.zero)
                {
                    Chunk chunk = World.Instance.GetChunkAt(hitPoint);

                    if (chunk != null)
                    {
                        Vector3Int chunkCoord = chunk.GetChunkData().ChunkCoordinate;
                        
                        // Use NetworkTerrainManager for all modifications, regardless of singleplayer/multiplayer
                        if (NetworkTerrainManager.Instance != null)
                        {
                            NetworkTerrainManager.Instance.RequestTerrainModification(chunkCoord, voxelPosition, isAdding);
                        }
                        else
                        {
                            // Fallback only if NetworkTerrainManager is missing (should rarely happen)
                            chunk.CompleteAllJobs();
                            chunk.DamageVoxel(voxelPosition, 3);
                        }
                    }
                }
            }
        }

        private void OnFootstep(AnimationEvent animationEvent)
        {
            if (animationEvent == null || animationEvent.animatorClipInfo.weight <= 0.5f)
                return;
                
            if (FootstepAudioClips == null || FootstepAudioClips.Length == 0)
                return;
                
            var index = Random.Range(0, FootstepAudioClips.Length);
            if (FootstepAudioClips[index] != null && _controller != null)
            {
                AudioSource.PlayClipAtPoint(FootstepAudioClips[index], 
                    transform.TransformPoint(_controller.center), 
                    FootstepAudioVolume);
            }
        }

        private void OnLand(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                AudioSource.PlayClipAtPoint(LandingAudioClip, transform.TransformPoint(_controller.center), FootstepAudioVolume);
            }
        }
    }
}