using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class WorldEntryUI : MonoBehaviour
{
    [SerializeField] private TMP_Text worldNameText;
    [SerializeField] private TMP_Text worldInfoText;
    [SerializeField] private Image selectionHighlight;
    [SerializeField] private Button selectButton;
    
    // Callback for selection
    private System.Action<string> onSelectCallback;
    
    // World data
    private string worldId;
    public string WorldId => worldId;
    
    public void Setup(WorldMetadata worldData, System.Action<string> selectCallback = null)
    {
        // Store data
        this.worldId = worldData.WorldId;
        this.onSelectCallback = selectCallback;
        
        // Update UI
        if (worldNameText != null)
        {
            worldNameText.text = worldData.WorldName;
        }
        
        if (worldInfoText != null)
        {
            string typeText = worldData.IsMultiplayerWorld ? "Multiplayer" : "Singleplayer";
            string dateText = worldData.LastPlayed.ToShortDateString();
            worldInfoText.text = $"{typeText} • Last played: {dateText}";
        }
        
        // Set selection highlighting off initially
        SetSelected(false);
        
        // Configure button
        if (selectButton != null)
        {
            selectButton.onClick.RemoveAllListeners();
            selectButton.onClick.AddListener(OnSelected);
        }
        else
        {
            // If no dedicated button, make this game object clickable
            Button entryButton = GetComponent<Button>();
            if (entryButton != null)
            {
                entryButton.onClick.RemoveAllListeners();
                entryButton.onClick.AddListener(OnSelected);
            }
            else if (TryGetComponent(out entryButton))
            {
                entryButton.onClick.RemoveAllListeners();
                entryButton.onClick.AddListener(OnSelected);
            }
            else
            {
                // Add a button component if none exists
                entryButton = gameObject.AddComponent<Button>();
                entryButton.onClick.AddListener(OnSelected);
            }
        }
    }
    
    private void OnSelected()
    {
        // Invoke the callback if provided
        onSelectCallback?.Invoke(worldId);
        
        // Highlight this item
        SetSelected(true);
    }
    
    public void SetSelected(bool selected)
    {
        // Update selection visual if available
        if (selectionHighlight != null)
        {
            selectionHighlight.gameObject.SetActive(selected);
        }
        else
        {
            // Fallback highlighting using the background color
            Image background = GetComponent<Image>();
            if (background != null)
            {
                Color color = background.color;
                
                if (selected)
                {
                    // Make the background more vibrant/highlighted
                    color.a = 1.0f;
                    background.color = color;
                }
                else
                {
                    // Make the background more transparent
                    color.a = 0.6f;
                    background.color = color;
                }
            }
        }
    }
}