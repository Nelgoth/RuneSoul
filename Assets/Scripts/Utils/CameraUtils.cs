using UnityEngine;
using Unity.Cinemachine;
using System.Collections.Generic;

/// <summary>
/// Utility methods for handling camera operations and lookups
/// </summary>
public static class CameraUtils
{
    private static Camera cachedMainCamera = null;
    private static Unity.Cinemachine.CinemachineBrain cachedBrain = null;
    private static Dictionary<GameObject, CinemachineCamera> cameraFollowTargets = new Dictionary<GameObject, CinemachineCamera>();
    
    /// <summary>
    /// Find the best camera transform for a GameObject to use
    /// Will search for cameras in this order:
    /// 1. Cinemachine cameras specifically targeting this GameObject
    /// 2. Main camera
    /// 3. CinemachineBrain
    /// 4. Any camera in the scene
    /// </summary>
    public static Transform GetBestCameraTransform(this GameObject gameObject, bool forceRefresh = false)
    {
        // First check if there's a camera specifically targeting this object or its children
        var targetCamera = FindCameraFollowingTarget(gameObject, forceRefresh);
        if (targetCamera != null)
        {
            return targetCamera.transform;
        }
        
        // Then check for main camera
        var mainCam = GetMainCamera(forceRefresh);
        if (mainCam != null)
        {
            return mainCam.transform;
        }
        
        // Check for CinemachineBrain
        var brain = GetCinemachineBrain(forceRefresh);
        if (brain != null)
        {
            return brain.transform;
        }
        
        // Last resort: any camera
        var cameras = Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (cameras.Length > 0)
        {
            return cameras[0].transform;
        }
        
        // No camera found
        Debug.LogWarning("No camera found in the scene!");
        return null;
    }
    
    /// <summary>
    /// Find the main camera, with caching for performance
    /// </summary>
    public static Camera GetMainCamera(bool forceRefresh = false)
    {
        if (cachedMainCamera == null || forceRefresh)
        {
            cachedMainCamera = Camera.main;
        }
        return cachedMainCamera;
    }
    
    /// <summary>
    /// Find the CinemachineBrain in the scene, with caching for performance
    /// </summary>
    public static CinemachineBrain GetCinemachineBrain(bool forceRefresh = false)
    {
        if (cachedBrain == null || forceRefresh || !cachedBrain.isActiveAndEnabled)
        {
            cachedBrain = Object.FindFirstObjectByType<CinemachineBrain>();
        }
        return cachedBrain;
    }
    
    /// <summary>
    /// Find if any CinemachineCamera is following the specified target
    /// </summary>
    public static CinemachineCamera FindCameraFollowingTarget(GameObject target, bool forceRefresh = false)
    {
        // Return from cache if available
        if (!forceRefresh && cameraFollowTargets.TryGetValue(target, out var cachedCamera))
        {
            if (cachedCamera != null && cachedCamera.isActiveAndEnabled)
            {
                return cachedCamera;
            }
        }
        
        // Look for a camera that follows this target
        var cameras = Object.FindObjectsByType<CinemachineCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var vcam in cameras)
        {
            if (vcam.Follow != null)
            {
                Transform followTransform = vcam.Follow;
                
                // Check if following this target directly
                if (followTransform == target.transform)
                {
                    cameraFollowTargets[target] = vcam;
                    return vcam;
                }
                
                // Check if following a child of this target
                if (followTransform.IsChildOf(target.transform))
                {
                    cameraFollowTargets[target] = vcam;
                    return vcam;
                }
            }
        }
        
        // Look for a camera that has this target as its LookAt
        foreach (var vcam in cameras)
        {
            if (vcam.LookAt != null)
            {
                Transform lookAtTransform = vcam.LookAt;
                
                // Check if looking at this target directly
                if (lookAtTransform == target.transform)
                {
                    cameraFollowTargets[target] = vcam;
                    return vcam;
                }
                
                // Check if looking at a child of this target
                if (lookAtTransform.IsChildOf(target.transform))
                {
                    cameraFollowTargets[target] = vcam;
                    return vcam;
                }
            }
        }
        
        // No camera found following this target
        return null;
    }
    
    /// <summary>
    /// Clear all cached camera references
    /// </summary>
    public static void ClearCache()
    {
        cachedMainCamera = null;
        cachedBrain = null;
        cameraFollowTargets.Clear();
    }
}