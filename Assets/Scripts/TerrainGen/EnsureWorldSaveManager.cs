// ADD this class to a new script file named EnsureWorldSaveManager.cs

using UnityEngine;

/// <summary>
/// Ensures WorldSaveManager exists and persists across scene changes.
/// Attach this to a GameObject in your MenuScene.
/// </summary>
public class EnsureWorldSaveManager : MonoBehaviour
{
    [SerializeField] private GameObject worldSaveManagerPrefab;
    [SerializeField] private bool createOnStart = true;
    
    private void Start()
    {
        if (createOnStart)
        {
            EnsureManagerExists();
        }
    }
    
    public void EnsureManagerExists()
    {
        // First check if it already exists
        WorldSaveManager existingManager = FindFirstObjectByType<WorldSaveManager>();
        
        if (existingManager == null)
        {
            Debug.Log("WorldSaveManager not found, creating a new instance");
            
            if (worldSaveManagerPrefab != null)
            {
                // Instantiate from prefab
                GameObject managerObj = Instantiate(worldSaveManagerPrefab);
                managerObj.name = "WorldSaveManager";
                DontDestroyOnLoad(managerObj);
                Debug.Log("Created WorldSaveManager from prefab");
            }
            else
            {
                // Create a new empty GameObject with WorldSaveManager component
                GameObject managerObj = new GameObject("WorldSaveManager");
                managerObj.AddComponent<WorldSaveManager>();
                DontDestroyOnLoad(managerObj);
                Debug.Log("Created WorldSaveManager manually");
            }
        }
        else
        {
            Debug.Log("Found existing WorldSaveManager");
            
            // Ensure it's set to DontDestroyOnLoad
            DontDestroyOnLoad(existingManager.gameObject);
        }
    }
}