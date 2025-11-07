using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Services.Lobbies.Models;

public class LobbyListEntry : MonoBehaviour
{
    [SerializeField] private TMP_Text lobbyNameText;
    [SerializeField] private TMP_Text lobbyInfoText;
    [SerializeField] private Button joinButton;
    
    private string lobbyCode;
    private Action<string> joinCallback;
    
    public void Setup(Lobby lobby, Action<string> onJoinLobby)
    {
        // Save references
        this.joinCallback = onJoinLobby;
        
        // Handle the lobby code - first try the standard code
        string code = lobby.LobbyCode;
        
        // If empty or invalid, try custom code from data
        if (string.IsNullOrWhiteSpace(code) && 
            lobby.Data != null && 
            lobby.Data.TryGetValue("CustomLobbyCode", out var customCodeData))
        {
            code = customCodeData.Value;
            Debug.Log($"Using custom lobby code: {code}");
        }
        
        // Still validate the code
        this.lobbyCode = ValidateAndCleanCode(code);
        
        // Set up UI elements
        SetupUI(lobby);
    }
    
    public void SetupWithCustomCode(Lobby lobby, string customCode, Action<string> onJoinLobby)
    {
        // Save references with the custom code
        this.lobbyCode = ValidateAndCleanCode(customCode);
        this.joinCallback = onJoinLobby;
        
        // Set up UI elements
        SetupUI(lobby);
    }

    // ADD this helper method to LobbyListEntry.cs
    private string ValidateAndCleanCode(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            Debug.LogWarning("Empty lobby code received");
            return string.Empty;
        }
        
        // Clean the code by removing problematic characters
        string cleaned = code.Replace("'", "").Replace(" ", "").Trim();
        
        // Log the cleaning/validation
        Debug.Log($"Lobby code validation: '{code}' → '{cleaned}'");
        
        return cleaned;
    }
    
    private void SetupUI(Lobby lobby)
    {
        if (lobbyNameText != null)
        {
            lobbyNameText.text = lobby.Name;
        }
        
        if (lobbyInfoText != null)
        {
            string visibilityText = lobby.IsPrivate ? "Private" : "Public";
            lobbyInfoText.text = $"Players: {lobby.Players.Count}/{lobby.MaxPlayers} • {visibilityText}";
        }
        
        // Set up button
        if (joinButton != null)
        {
            joinButton.onClick.RemoveAllListeners();
            joinButton.onClick.AddListener(OnJoinButtonClicked);
        }
        else
        {
            // If no dedicated join button, make the whole entry clickable
            Button entryButton = GetComponent<Button>();
            if (entryButton != null)
            {
                entryButton.onClick.RemoveAllListeners();
                entryButton.onClick.AddListener(OnJoinButtonClicked);
            }
            else
            {
                entryButton = gameObject.AddComponent<Button>();
                entryButton.onClick.AddListener(OnJoinButtonClicked);
            }
        }
    }
    
    private void OnJoinButtonClicked()
    {
        // Ensure we have a valid lobby code
        if (string.IsNullOrWhiteSpace(lobbyCode))
        {
            Debug.LogError("Cannot join lobby: lobbyCode is null or empty");
            return;
        }
        
        Debug.Log($"Join button clicked for lobby with code: {lobbyCode}");
        
        // Invoke the join callback
        joinCallback?.Invoke(lobbyCode);
    }
}