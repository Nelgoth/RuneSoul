using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;

/// <summary>
/// UI for the building menu - allows players to select building pieces
/// Similar to Valheim's build menu
/// </summary>
public class BuildingMenuUI : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject menuPanel;
    [SerializeField] private Transform pieceButtonContainer;
    [SerializeField] private GameObject pieceButtonPrefab;
    [SerializeField] private TextMeshProUGUI selectedPieceNameText;
    [SerializeField] private TextMeshProUGUI selectedPieceDescriptionText;
    [SerializeField] private Image selectedPieceIcon;
    
    [Header("Settings")]
    [SerializeField] private KeyCode toggleKey = KeyCode.Tab;
    
    private BuildingSystem buildingSystem;
    private BuildingPiece[] availablePieces;
    private BuildingPiece selectedPiece;
    
    public event Action<BuildingPiece> OnPieceSelected;
    
    private void Awake()
    {
        if (menuPanel != null)
        {
            menuPanel.SetActive(false);
        }
    }
    
    public void Initialize(BuildingSystem system, BuildingPiece[] pieces)
    {
        buildingSystem = system;
        availablePieces = pieces;
        
        CreatePieceButtons();
    }
    
    private void CreatePieceButtons()
    {
        if (pieceButtonContainer == null || pieceButtonPrefab == null || availablePieces == null) return;
        
        // Clear existing buttons
        foreach (Transform child in pieceButtonContainer)
        {
            Destroy(child.gameObject);
        }
        
        // Create button for each piece
        foreach (var piece in availablePieces)
        {
            if (piece == null) continue;
            
            GameObject buttonObj = Instantiate(pieceButtonPrefab, pieceButtonContainer);
            Button button = buttonObj.GetComponent<Button>();
            
            if (button == null)
            {
                button = buttonObj.AddComponent<Button>();
            }
            
            // Set icon if available
            Image iconImage = buttonObj.GetComponentInChildren<Image>();
            if (iconImage != null && piece.icon != null)
            {
                iconImage.sprite = piece.icon;
            }
            
            // Set text
            TextMeshProUGUI text = buttonObj.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null)
            {
                text.text = piece.pieceName;
            }
            
            // Add click listener
            button.onClick.AddListener(() => SelectPiece(piece));
        }
    }
    
    public void SelectPiece(BuildingPiece piece)
    {
        selectedPiece = piece;
        
        // Update UI
        if (selectedPieceNameText != null)
        {
            selectedPieceNameText.text = piece.pieceName;
        }
        
        if (selectedPieceDescriptionText != null)
        {
            selectedPieceDescriptionText.text = piece.description;
        }
        
        if (selectedPieceIcon != null && piece.icon != null)
        {
            selectedPieceIcon.sprite = piece.icon;
        }
        
        // Notify listeners
        OnPieceSelected?.Invoke(piece);
    }
    
    public void ToggleMenu()
    {
        if (menuPanel != null)
        {
            bool isActive = menuPanel.activeSelf;
            menuPanel.SetActive(!isActive);
            
            // Pause/unpause game or disable player input when menu is open
            // This depends on your game's input system
        }
    }
    
    public void ShowMenu()
    {
        if (menuPanel != null)
        {
            menuPanel.SetActive(true);
        }
    }
    
    public void HideMenu()
    {
        if (menuPanel != null)
        {
            menuPanel.SetActive(false);
        }
    }
    
    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            ToggleMenu();
        }
    }
}




