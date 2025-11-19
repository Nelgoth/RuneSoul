using UnityEngine;
using UnityEditor;
using System.IO;

[InitializeOnLoad]
public class InputActionsSetup
{
    static InputActionsSetup()
    {
        // This will run when Unity loads/compiles
        EditorApplication.delayCall += ConfigureInputActions;
    }

    static void ConfigureInputActions()
    {
        string assetPath = "Assets/PlayerControls.inputactions";
        
        if (!File.Exists(assetPath))
        {
            Debug.LogWarning("PlayerControls.inputactions not found at: " + assetPath);
            return;
        }

        // Import the asset to make sure Unity recognizes it
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        
        // Try to get the importer and configure it
        var importer = AssetImporter.GetAtPath(assetPath);
        if (importer != null)
        {
            // Force reimport to ensure settings are applied
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ImportRecursive);
            Debug.Log("PlayerControls.inputactions has been configured. If errors persist, please:");
            Debug.Log("1. Select PlayerControls.inputactions in the Project window");
            Debug.Log("2. In the Inspector, check 'Generate C# Class'");
            Debug.Log("3. Click 'Apply'");
        }
        else
        {
            Debug.LogError("Could not get importer for PlayerControls.inputactions");
        }
    }

    [MenuItem("Tools/Configure Input Actions")]
    static void ManualConfigure()
    {
        ConfigureInputActions();
    }

    [MenuItem("Tools/Reimport All Assets")]
    static void ReimportAll()
    {
        Debug.Log("Reimporting all assets...");
        AssetDatabase.ImportAsset("Assets", ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh();
        Debug.Log("Reimport complete!");
    }
}

