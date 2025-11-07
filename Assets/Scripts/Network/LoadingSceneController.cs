using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Controls the loading scene UI and transitions
/// </summary>
public class LoadingSceneController : MonoBehaviour
{
    [Header("Loading UI")]
    [SerializeField] private Slider progressBar;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text progressText;
    [SerializeField] private GameObject errorPanel;
    [SerializeField] private TMP_Text errorText;
    [SerializeField] private Button returnButton;

    [Header("Visual Settings")]
    [SerializeField] private float minLoadingTime = 1.5f; // Minimum time to show loading screen
    [SerializeField] private float transitionSpeed = 2.0f; // Speed of progress bar animation
    
    private float targetProgress = 0f;
    private float currentProgress = 0f;
    private bool isLoading = true;

    private void Awake()
    {
        // Initialize UI elements
        if (progressBar != null) progressBar.value = 0f;
        if (statusText != null) statusText.text = "Initializing...";
        if (progressText != null) progressText.text = "0%";
        
        // Hide error panel initially
        if (errorPanel != null) errorPanel.SetActive(false);
        
        // Set up return button
        if (returnButton != null)
        {
            returnButton.onClick.AddListener(OnReturnButtonClicked);
        }
    }

    private void Start()
    {
        Debug.Log("LoadingSceneController: Start method called");
        
        // Initialize UI elements
        if (progressBar != null) progressBar.value = 0f;
        if (statusText != null) statusText.text = "Initializing...";
        if (progressText != null) progressText.text = "0%";
        
        // Hide error panel initially
        if (errorPanel != null) errorPanel.SetActive(false);
        
        // Set up return button
        if (returnButton != null)
        {
            returnButton.onClick.AddListener(OnReturnButtonClicked);
        }
        
        // Check if GameManager exists
        if (GameManager.Instance == null)
        {
            Debug.LogError("LoadingSceneController: GameManager.Instance is null!");
            ShowError("GameManager not found");
            return;
        }
        
        // Start minimum loading time coroutine
        StartCoroutine(EnsureMinimumLoadingTime());
        
        Debug.Log("LoadingSceneController: Started minimum loading time coroutine");
    }

    private void Update()
    {
        // Smooth progress bar animation
        if (currentProgress < targetProgress)
        {
            currentProgress = Mathf.MoveTowards(currentProgress, targetProgress, transitionSpeed * Time.deltaTime);
            UpdateProgressUI(currentProgress);
        }
        
        // Check for errors from GameManager
        if (GameManager.Instance != null && GameManager.Instance.HasConnectionError())
        {
            ShowError(GameManager.Instance.GetConnectionError());
        }
    }

    /// <summary>
    /// Update the loading progress with new value and status
    /// </summary>
    public void UpdateProgress(float progress, string status)
    {
        Debug.Log($"LoadingSceneController: Progress update - {progress:P0} - {status}");
        targetProgress = Mathf.Clamp01(progress);
        
        if (statusText != null)
        {
            statusText.text = status;
        }
        
        // Update UI immediately
        UpdateProgressUI(progress);
    }

    /// <summary>
    /// Update UI elements to match current progress
    /// </summary>
    private void UpdateProgressUI(float progress)
    {
        if (progressBar != null)
        {
            progressBar.value = progress;
        }
        
        if (progressText != null)
        {
            int percentage = Mathf.RoundToInt(progress * 100);
            progressText.text = $"{percentage}%";
        }
    }

    /// <summary>
    /// Show error message and error panel
    /// </summary>
    public void ShowError(string message)
    {
        isLoading = false;
        
        if (errorText != null)
        {
            errorText.text = message;
        }
        
        if (errorPanel != null)
        {
            errorPanel.SetActive(true);
        }
    }

    /// <summary>
    /// Handle return button click
    /// </summary>
    private void OnReturnButtonClicked()
    {
        if (GameManager.Instance != null)
        {
            GameManager.Instance.ResetErrorState();
            UnityEngine.SceneManagement.SceneManager.LoadScene("MenuScene");
        }
    }

    /// <summary>
    /// Ensure the loading screen stays visible for at least the minimum time
    /// </summary>
    private IEnumerator EnsureMinimumLoadingTime()
    {
        Debug.Log("LoadingSceneController: Starting minimum loading time coroutine");
        float startTime = Time.time;
        
        while (isLoading && (Time.time - startTime < minLoadingTime || currentProgress < 1.0f))
        {
            // Every second, log our status
            if (Mathf.FloorToInt(Time.time) % 2 == 0)
            {
                Debug.Log($"LoadingSceneController: Still loading - Progress: {currentProgress:P0}, Time elapsed: {Time.time - startTime:F1}s");
            }
            yield return null;
        }
        
        Debug.Log($"LoadingSceneController: Finished waiting - isLoading: {isLoading}, time elapsed: {Time.time - startTime:F1}s, progress: {currentProgress:P0}");
        
        // If we're still loading after minimum time (no errors), complete loading
        if (isLoading && errorPanel != null && !errorPanel.activeSelf)
        {
            // Final update to ensure we reach 100%
            currentProgress = 1.0f;
            UpdateProgressUI(currentProgress);
            Debug.Log("LoadingSceneController: Loading completed successfully");
            
            // Notify any GameUIManager instances that loading is complete
            var uiManager = FindObjectOfType<GameUIManager>();
            if (uiManager != null)
            {
                Debug.Log("LoadingSceneController: Notifying GameUIManager of loading completion");
                // Use SendMessage to call a method by name - this avoids direct dependencies
                uiManager.SendMessage("ShowGameplayScreen", SendMessageOptions.DontRequireReceiver);
            }
            else
            {
                Debug.LogWarning("LoadingSceneController: No GameUIManager found to notify");
            }
        }
    }
}