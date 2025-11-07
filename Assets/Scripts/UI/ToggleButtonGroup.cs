using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Manages a group of toggle buttons where only one can be active at a time
/// </summary>
public class ToggleButtonGroup : MonoBehaviour
{
    [SerializeField] private List<Button> buttons;
    [SerializeField] private Sprite activeSprite;
    [SerializeField] private Sprite inactiveSprite;
    [SerializeField] private Color activeTextColor = Color.white;
    [SerializeField] private Color inactiveTextColor = new Color(0.8f, 0.8f, 0.8f);
    [SerializeField] private int defaultActiveIndex = 0;
    [SerializeField] private bool useButtonImages = true;
    [SerializeField] private bool useTextColors = true;
    
    private int currentActiveIndex = -1;
    
    // Event delegate for button toggle
    public delegate void ButtonToggledHandler(int index);
    public event ButtonToggledHandler OnButtonToggled;
    
    private void Awake()
    {
        Setup();
    }
    
    public void Setup()
    {
        // Remove any existing listeners to avoid duplicates
        for (int i = 0; i < buttons.Count; i++)
        {
            Button button = buttons[i];
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                
                // Add new listener
                int index = i; // Capture index for closure
                button.onClick.AddListener(() => ToggleButton(index));
            }
        }
        
        // Set default active button
        SetActiveButton(defaultActiveIndex);
    }
    
    public void ToggleButton(int index)
    {
        if (index < 0 || index >= buttons.Count) return;
        
        // Set this button as active
        SetActiveButton(index);
        
        // Notify listeners
        OnButtonToggled?.Invoke(index);
    }
    
    public void SetActiveButton(int index)
    {
        if (index < 0 || index >= buttons.Count) return;
        
        // Skip if already active
        if (currentActiveIndex == index) return;
        
        // Deactivate the previous button
        if (currentActiveIndex >= 0 && currentActiveIndex < buttons.Count)
        {
            SetButtonState(buttons[currentActiveIndex], false);
        }
        
        // Activate the new button
        SetButtonState(buttons[index], true);
        
        // Update current index
        currentActiveIndex = index;
    }
    
    private void SetButtonState(Button button, bool active)
    {
        if (button == null) return;
        
        // Update image sprite if available
        if (useButtonImages && activeSprite != null && inactiveSprite != null)
        {
            Image buttonImage = button.GetComponent<Image>();
            if (buttonImage != null)
            {
                buttonImage.sprite = active ? activeSprite : inactiveSprite;
            }
        }
        
        // Update text color if available
        if (useTextColors)
        {
            TMPro.TMP_Text buttonText = button.GetComponentInChildren<TMPro.TMP_Text>();
            if (buttonText != null)
            {
                buttonText.color = active ? activeTextColor : inactiveTextColor;
            }
        }
        
        // Make button non-interactable when active (visual indicator)
        button.interactable = !active;
    }
    
    public int GetActiveIndex()
    {
        return currentActiveIndex;
    }
}