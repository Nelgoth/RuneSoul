using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using System.Linq;

public class EnhancedBenchmarkManager : MonoBehaviour
{
    public static EnhancedBenchmarkManager Instance { get; private set; }
    
    // Generation metrics (pure chunk generation)
    private class GenerationMetrics
    {
        public double totalTime;
        public int count;
        public Dictionary<string, double> phaseTotals = new Dictionary<string, double>();
        public Queue<double> recentTimes = new Queue<double>();
        public const int MAX_HISTORY = 50;
    }

    // System metrics (total system overhead)
    private class SystemMetrics
    {
        public double totalOperationTime;
        public int operationCount;
        public Dictionary<string, double> operationTotals = new Dictionary<string, double>();
        public Queue<SystemTimingEntry> recentOperations = new Queue<SystemTimingEntry>();
        public const int MAX_HISTORY = 100;
    }

    private class SystemTimingEntry
    {
        public DateTime startTime;
        public DateTime endTime;
        public string operationType;
        public Vector3Int chunkCoord;
        public double queueTime;  // Time spent in queue
        public double processingTime;  // Time in actual processing
        public double totalTime => (endTime - startTime).TotalMilliseconds;
    }

    [Header("Display Settings")]
    public bool showDisplay = true;
    public float updateInterval = 0.5f;
    private float lastUpdateTime;
    private string currentDisplayText;
    private Rect displayRect = new Rect(10, 10, 500, 600);
    private Vector2 scrollPosition;
    private GUIStyle textStyle;

    private Dictionary<Vector3Int, SystemTimingEntry> activeOperations = 
        new Dictionary<Vector3Int, SystemTimingEntry>();
    private GenerationMetrics generationMetrics = new GenerationMetrics();
    private SystemMetrics systemMetrics = new SystemMetrics();
    private readonly object metricsLock = new object();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        if (Time.time - lastUpdateTime > updateInterval)
        {
            currentDisplayText = GetDetailedReport();
            lastUpdateTime = Time.time;
        }
    }

    private void OnGUI()
    {
        if (!showDisplay) return;

        if (textStyle == null)
        {
            textStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true
            };
            textStyle.normal.textColor = Color.white;
        }

        // Create semi-transparent background
        var boxStyle = new GUIStyle(GUI.skin.box);
        var backgroundColor = new Color(0, 0, 0, 0.7f);
        var backgroundTexture = new Texture2D(1, 1);
        backgroundTexture.SetPixel(0, 0, backgroundColor);
        backgroundTexture.Apply();
        boxStyle.normal.background = backgroundTexture;
        
        GUI.Box(displayRect, "", boxStyle);

        // Create scrollview
        float textHeight = textStyle.CalcHeight(new GUIContent(currentDisplayText), 480);
        scrollPosition = GUI.BeginScrollView(
            displayRect,
            scrollPosition,
            new Rect(0, 0, 480, Mathf.Max(600, textHeight))
        );

        // Draw the text
        GUI.Label(new Rect(5, 5, 470, textHeight), currentDisplayText, textStyle);

        GUI.EndScrollView();
        
        // Clean up the temporary texture
        Destroy(backgroundTexture);
    }

    public void RecordTerrainAnalysis(Vector3Int chunkCoord, bool isEmpty, bool isSolid)
    {
        if (Time.frameCount % 300 == 0)  // Only log every ~5 seconds
        {
            Debug.Log($"Terrain Analysis - Chunk {chunkCoord}: " +
                    $"{(isEmpty ? "Empty" : isSolid ? "Solid" : "Mixed")}");
        }
    }

    public void BeginOperation(Vector3Int chunkCoord, string operationType)
    {
        lock (metricsLock)
        {
            var entry = new SystemTimingEntry
            {
                startTime = DateTime.UtcNow,
                operationType = operationType,
                chunkCoord = chunkCoord
            };
            activeOperations[chunkCoord] = entry;
        }
    }

    public void EndOperation(Vector3Int chunkCoord)
    {
        lock (metricsLock)
        {
            if (activeOperations.TryGetValue(chunkCoord, out SystemTimingEntry entry))
            {
                entry.endTime = DateTime.UtcNow;
                activeOperations.Remove(chunkCoord);

                systemMetrics.totalOperationTime += entry.totalTime;
                systemMetrics.operationCount++;

                if (!systemMetrics.operationTotals.ContainsKey(entry.operationType))
                    systemMetrics.operationTotals[entry.operationType] = 0;
                systemMetrics.operationTotals[entry.operationType] += entry.totalTime;

                systemMetrics.recentOperations.Enqueue(entry);
                while (systemMetrics.recentOperations.Count > SystemMetrics.MAX_HISTORY)
                    systemMetrics.recentOperations.Dequeue();
            }
        }
    }

    public void RecordGenerationPhase(string phaseName, double duration)
    {
        lock (metricsLock)
        {
            generationMetrics.totalTime += duration;
            generationMetrics.count++;

            if (!generationMetrics.phaseTotals.ContainsKey(phaseName))
                generationMetrics.phaseTotals[phaseName] = 0;
            generationMetrics.phaseTotals[phaseName] += duration;

            generationMetrics.recentTimes.Enqueue(duration);
            while (generationMetrics.recentTimes.Count > GenerationMetrics.MAX_HISTORY)
                generationMetrics.recentTimes.Dequeue();
        }
    }

    public string GetDetailedReport()
    {
        lock (metricsLock)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== Enhanced Benchmark Report ===\n");

            // Generation Metrics
            sb.AppendLine("Pure Generation Metrics:");
            if (generationMetrics.count > 0)
            {
                double avgTotal = generationMetrics.totalTime / generationMetrics.count;
                sb.AppendLine($"Average Total Generation Time: {avgTotal:F2}ms");
                sb.AppendLine($"Total Chunks Generated: {generationMetrics.count}");
                
                sb.AppendLine("\nPhase Breakdown:");
                foreach (var phase in generationMetrics.phaseTotals)
                {
                    double avgPhase = phase.Value / generationMetrics.count;
                    double percent = (phase.Value / generationMetrics.totalTime) * 100;
                    sb.AppendLine($"  {phase.Key}: {avgPhase:F2}ms ({percent:F1}%)");
                }

                if (generationMetrics.recentTimes.Count > 0)
                {
                    double recentAvg = generationMetrics.recentTimes.Average();
                    sb.AppendLine($"\nRecent Average (last {generationMetrics.recentTimes.Count} generations): {recentAvg:F2}ms");
                }
            }

            // System Metrics
            sb.AppendLine("\nTotal System Metrics (including overhead):");
            if (systemMetrics.operationCount > 0)
            {
                double avgTotal = systemMetrics.totalOperationTime / systemMetrics.operationCount;
                sb.AppendLine($"Average Total Operation Time: {avgTotal:F2}ms");
                sb.AppendLine($"Total Operations: {systemMetrics.operationCount}");

                sb.AppendLine("\nOperation Type Breakdown:");
                foreach (var op in systemMetrics.operationTotals)
                {
                    double avgOp = op.Value / systemMetrics.operationCount;
                    double percent = (op.Value / systemMetrics.totalOperationTime) * 100;
                    sb.AppendLine($"  {op.Key}: {avgOp:F2}ms ({percent:F1}%)");
                }

                if (systemMetrics.recentOperations.Count > 0)
                {
                    var recentAvg = systemMetrics.recentOperations
                        .Average(op => op.totalTime);
                    sb.AppendLine($"\nRecent Average (last {systemMetrics.recentOperations.Count} operations): {recentAvg:F2}ms");
                }
            }

            // Current Active Operations
            if (activeOperations.Count > 0)
            {
                sb.AppendLine($"\nCurrently Active Operations: {activeOperations.Count}");
                foreach (var op in activeOperations)
                {
                    var duration = (DateTime.UtcNow - op.Value.startTime).TotalMilliseconds;
                    sb.AppendLine($"  {op.Value.operationType} on chunk {op.Key}: {duration:F2}ms so far");
                }
            }
            sb.Append(GetChunkCountDiagnostics());
            return sb.ToString();
        }
    }

    private string GetChunkCountDiagnostics()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("\n=== Chunk Count Diagnostics ===");
        
        // Check World's active chunks
        int worldActiveChunks = World.Instance.ActiveChunkCount;
        sb.AppendLine($"Active Chunks in World: {worldActiveChunks}");
        
        // Check ChunkPool stats
        int poolAvailable = ChunkPoolManager.Instance.GetAvailableCount();
        sb.AppendLine($"Available Chunks in Pool: {poolAvailable}");
        
        // Check total operations vs unique chunks
        var uniqueChunks = systemMetrics.recentOperations
            .Select(op => op.chunkCoord)
            .Distinct()
            .Count();
        sb.AppendLine($"Unique Chunks in Recent Operations: {uniqueChunks}");
        sb.AppendLine($"Total Operations Recorded: {systemMetrics.operationCount}");
        
        return sb.ToString();
    }

    public void ClearMetrics()
    {
        lock (metricsLock)
        {
            generationMetrics = new GenerationMetrics();
            systemMetrics = new SystemMetrics();
            activeOperations.Clear();
        }
    }

    private void OnDestroy()
    {
        Instance = null;
    }
}