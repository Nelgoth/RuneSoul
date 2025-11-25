using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using NelsUtils;
using System.Linq;

public class ChunkStateManager : MonoBehaviour
{
    public static ChunkStateManager Instance { get; private set; }
    
    private readonly Dictionary<Vector3Int, ChunkState> chunkStates = new Dictionary<Vector3Int, ChunkState>();
    private readonly object stateLock = new object();
    private const int MAX_ERROR_HISTORY = 10;
    
    private readonly ConcurrentDictionary<Vector3Int, Queue<ChunkConfigurations.ChunkError>> errorHistory 
        = new ConcurrentDictionary<Vector3Int, Queue<ChunkConfigurations.ChunkError>>();
    
    public HashSet<Vector3Int> QuarantinedChunks { get; } = new HashSet<Vector3Int>();

    private bool ShouldLogStateTransitions =>
        World.Instance != null &&
        World.Instance.Config != null &&
        World.Instance.Config.enableChunkStateLogs;

    public class ChunkState
    {
        public ChunkConfigurations.ChunkStatus Status { get; private set; }
        public ChunkConfigurations.ChunkStateFlags Flags { get; private set; }
        public DateTime LastStatusChange { get; private set; }
        public string ErrorMessage { get; private set; }
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public int RetryCount { get; private set; }

        public ChunkState(ChunkConfigurations.ChunkStatus status, ChunkConfigurations.ChunkStateFlags flags)
        {
            Status = status;
            Flags = flags;
            LastStatusChange = DateTime.UtcNow;
            RetryCount = 0;
        }

        public void SetError(string message)
        {
            ErrorMessage = message;
            Flags |= ChunkConfigurations.ChunkStateFlags.Error;
            RetryCount++;
        }

        public void UpdateState(ChunkConfigurations.ChunkStatus newStatus, ChunkConfigurations.ChunkStateFlags newFlags)
        {
            Status = newStatus;
            Flags = newFlags;
            LastStatusChange = DateTime.UtcNow;
        }

        public void ResetError()
        {
            ErrorMessage = null;
            Flags &= ~ChunkConfigurations.ChunkStateFlags.Error;
        }
    }

    private readonly Dictionary<(ChunkConfigurations.ChunkStatus From, ChunkConfigurations.ChunkStatus To), bool> 
        validTransitions = new Dictionary<(ChunkConfigurations.ChunkStatus From, ChunkConfigurations.ChunkStatus To), bool>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        InitializeValidTransitions();
        StateChanged += HandleStateChange;
    }

    private void InitializeValidTransitions()
    {
        // Basic lifecycle
        validTransitions.Add((ChunkConfigurations.ChunkStatus.None, ChunkConfigurations.ChunkStatus.Loading), true);
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Loading, ChunkConfigurations.ChunkStatus.Loaded), true);
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Loaded, ChunkConfigurations.ChunkStatus.Unloading), true);
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Unloading, ChunkConfigurations.ChunkStatus.Unloaded), true);
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Unloaded, ChunkConfigurations.ChunkStatus.Loading), true);
        
        // CRITICAL FIX: Add missing transition path
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Unloaded, ChunkConfigurations.ChunkStatus.Loaded), true);
        
        // Modification paths
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Loaded, ChunkConfigurations.ChunkStatus.Modified), true);
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Loading, ChunkConfigurations.ChunkStatus.Modified), true);
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Modified, ChunkConfigurations.ChunkStatus.Saving), true);
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Saving, ChunkConfigurations.ChunkStatus.Saved), true);
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Saved, ChunkConfigurations.ChunkStatus.Unloading), true);
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Modified, ChunkConfigurations.ChunkStatus.Unloading), true);
        
        // Recovery paths
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Error, ChunkConfigurations.ChunkStatus.Loading), true);
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Error, ChunkConfigurations.ChunkStatus.Unloading), true);
        validTransitions.Add((ChunkConfigurations.ChunkStatus.Error, ChunkConfigurations.ChunkStatus.None), true);
    }

    public bool IsValidTransition(ChunkConfigurations.ChunkStatus from, ChunkConfigurations.ChunkStatus to)
    {
        return validTransitions.TryGetValue((from, to), out bool isValid) && isValid;
    }

    public bool TryChangeState(Vector3Int chunkCoord, ChunkConfigurations.ChunkStatus newStatus, 
        ChunkConfigurations.ChunkStateFlags newFlags)
    {
        lock (stateLock)
        {
            if (!chunkStates.TryGetValue(chunkCoord, out var currentState))
            {
                currentState = new ChunkState(ChunkConfigurations.ChunkStatus.None, 
                    ChunkConfigurations.ChunkStateFlags.None);
                chunkStates[chunkCoord] = currentState;
            }

            // Store initial values
            ChunkConfigurations.ChunkStatus initialStatus = currentState.Status;
            ChunkConfigurations.ChunkStateFlags initialFlags = currentState.Flags;

            // DEBUG: Log state change attempt
            if (ShouldLogStateTransitions)
            {
                Debug.Log($"[StateManager] Attempting state change for chunk {chunkCoord}: {initialStatus} -> {newStatus}");
            }

            // CRITICAL FIX: Skip validation and update if state and flags are the same
            // This simplifies idempotent state setting operations
            if (initialStatus == newStatus && initialFlags == newFlags)
            {
                if (ShouldLogStateTransitions)
                {
                    Debug.Log($"[StateManager] Chunk {chunkCoord} already in requested state {newStatus} with flags {newFlags}, skipping update");
                }
                return true;
            }

            // Always allow transition to Error status
            if (newStatus == ChunkConfigurations.ChunkStatus.Error)
            {
                currentState.UpdateState(newStatus, newFlags | ChunkConfigurations.ChunkStateFlags.Error);
                StateChanged?.Invoke(chunkCoord, initialStatus, initialFlags, newStatus, newFlags);
                return true;
            }

            // Check for valid state transition
            if (!ValidateStateTransition(currentState.Status, newStatus))
            {
                string errorMsg = $"Invalid state transition for chunk {chunkCoord} from {currentState.Status} to {newStatus}";
                Debug.LogWarning(errorMsg);
                
                // DEBUG: Add stack trace to identify the source
                Debug.LogWarning($"State transition stack trace: {Environment.StackTrace}");
                
                currentState.SetError(errorMsg);
                
                if (!QuarantinedChunks.Contains(chunkCoord))
                {
                    QuarantinedChunks.Add(chunkCoord);
                    LogChunkError(chunkCoord, new ChunkConfigurations.ChunkError {
                        Message = errorMsg,
                        Status = currentState.Status,
                        Timestamp = DateTime.UtcNow,
                        RetryCount = currentState.RetryCount
                    });
                }
                
                return false;
            }

            try
            {
                currentState.UpdateState(newStatus, newFlags);
                currentState.ResetError();
                
                if (QuarantinedChunks.Contains(chunkCoord))
                {
                    QuarantinedChunks.Remove(chunkCoord);
                }

                StateChanged?.Invoke(chunkCoord, initialStatus, initialFlags, newStatus, newFlags);
                
                // DEBUG: Log successful state change
                if (ShouldLogStateTransitions)
                {
                    Debug.Log($"[StateManager] Successfully changed state for chunk {chunkCoord}: {initialStatus} -> {newStatus}");
                }
                
                return true;
            }
            catch (Exception e)
            {
                string errorMsg = $"State transition failed for chunk {chunkCoord}: {e.Message}";
                Debug.LogError(errorMsg);
                currentState.SetError(errorMsg);
                
                LogChunkError(chunkCoord, new ChunkConfigurations.ChunkError {
                    Message = errorMsg,
                    Status = initialStatus,
                    Timestamp = DateTime.UtcNow,
                    RetryCount = currentState.RetryCount,
                    StackTrace = e.StackTrace
                });
                
                return false;
            }
        }
    }

    public void LogChunkError(Vector3Int coord, ChunkConfigurations.ChunkError error)
    {
        errorHistory.AddOrUpdate(coord,
            new Queue<ChunkConfigurations.ChunkError>(new[] { error }),
            (_, queue) => {
                if (queue.Count >= MAX_ERROR_HISTORY)
                    queue.Dequeue();
                queue.Enqueue(error);
                return queue;
            });
    }

    private void HandleStateChange(Vector3Int chunkCoord, ChunkConfigurations.ChunkStatus oldStatus, 
        ChunkConfigurations.ChunkStateFlags oldFlags, ChunkConfigurations.ChunkStatus newStatus, 
        ChunkConfigurations.ChunkStateFlags newFlags)
    {
        // If transitioning to Modified, ensure we save
        if (newStatus == ChunkConfigurations.ChunkStatus.Modified)
        {
            if (World.Instance.TryGetChunk(chunkCoord, out Chunk chunk))
            {
                // CRITICAL: Complete any pending jobs before saving
                chunk.CompleteAllJobs();
                chunk.GetChunkData()?.SaveData();
            }
        }
    }

    public ChunkState GetChunkState(Vector3Int chunkCoord)
    {
        lock (stateLock)
        {
            if (!chunkStates.TryGetValue(chunkCoord, out var state))
            {
                state = new ChunkState(ChunkConfigurations.ChunkStatus.None, 
                    ChunkConfigurations.ChunkStateFlags.None);
                chunkStates[chunkCoord] = state;
            }
            return state;
        }
    }

    private bool ValidateStateTransition(ChunkConfigurations.ChunkStatus from, ChunkConfigurations.ChunkStatus to)
    {
        // CRITICAL FIX: If the state isn't changing, we should treat it as valid
        // This prevents errors when code tries to ensure a specific state
        if (from == to)
        {
            if (ShouldLogStateTransitions)
            {
                Debug.Log($"State transition to same state: {from} -> {to} (treated as valid)");
            }
            return true;
        }

        bool isValid = validTransitions.TryGetValue((from, to), out bool value) && value;
        if (!isValid)
        {
            Debug.LogWarning($"Invalid state transition attempted: {from} -> {to}");
            
            // Log stack trace to identify source of invalid transitions
            Debug.LogWarning($"State transition stack trace: {Environment.StackTrace}");
            
            // Log all valid transitions from current state
            var validFromCurrent = validTransitions
                .Where(kvp => kvp.Key.From == from && kvp.Value)
                .Select(kvp => kvp.Key.To);
                
            Debug.LogWarning($"Valid transitions from {from}: {string.Join(", ", validFromCurrent)}");
        }
        return isValid;
    }

    public Queue<ChunkConfigurations.ChunkError> GetErrorHistory(Vector3Int chunkCoord)
    {
        return errorHistory.TryGetValue(chunkCoord, out var history) ? 
            new Queue<ChunkConfigurations.ChunkError>(history) : 
            new Queue<ChunkConfigurations.ChunkError>();
    }

    public event Action<Vector3Int, ChunkConfigurations.ChunkStatus, ChunkConfigurations.ChunkStateFlags, 
        ChunkConfigurations.ChunkStatus, ChunkConfigurations.ChunkStateFlags> StateChanged;

    private void OnDestroy()
    {
        StateChanged -= HandleStateChange;
    }
}