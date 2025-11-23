using UnityEngine;
using System.Text;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Diagnostic tool to monitor terrain optimization systems
/// Press F3 to toggle diagnostic overlay
/// </summary>
public class TerrainOptimizationDiagnostics : MonoBehaviour
{
    [Header("Display Settings")]
    [SerializeField] private bool showDiagnostics = true;
    [SerializeField] private KeyCode toggleKey = KeyCode.F3;
    [SerializeField] private int fontSize = 14;
    
    private GUIStyle guiStyle;
    private float updateInterval = 0.5f;
    private float lastUpdateTime;
    
    // Performance tracking
    private float[] fpsHistory = new float[60];
    private int fpsHistoryIndex = 0;
    private float avgFPS = 60f;
    private float minFPS = 60f;
    private float maxFPS = 60f;
    
    // Chunk tracking
    private int lastFrameChunkCount = 0;
    private int chunksLoadedLastSecond = 0;
    private int chunksUnloadedLastSecond = 0;
    private float lastChunkCountUpdate = 0f;
    
    private void Update()
    {
        // Toggle display
        if (Input.GetKeyDown(toggleKey))
        {
            showDiagnostics = !showDiagnostics;
            Debug.Log($"[Diagnostics] Display {(showDiagnostics ? "enabled" : "disabled")}");
        }
        
        // Update FPS tracking
        float currentFPS = 1f / Time.unscaledDeltaTime;
        fpsHistory[fpsHistoryIndex] = currentFPS;
        fpsHistoryIndex = (fpsHistoryIndex + 1) % fpsHistory.Length;
        
        // Calculate FPS stats
        if (Time.time - lastUpdateTime >= updateInterval)
        {
            avgFPS = fpsHistory.Average();
            minFPS = fpsHistory.Min();
            maxFPS = fpsHistory.Max();
            lastUpdateTime = Time.time;
        }
        
        // Track chunk loading/unloading
        if (World.Instance != null)
        {
            int currentChunkCount = World.Instance.GetLoadedChunkCount();
            
            if (Time.time - lastChunkCountUpdate >= 1f)
            {
                int delta = currentChunkCount - lastFrameChunkCount;
                if (delta > 0)
                    chunksLoadedLastSecond = delta;
                else if (delta < 0)
                    chunksUnloadedLastSecond = -delta;
                else
                {
                    chunksLoadedLastSecond = 0;
                    chunksUnloadedLastSecond = 0;
                }
                
                lastFrameChunkCount = currentChunkCount;
                lastChunkCountUpdate = Time.time;
            }
        }
    }
    
    private void OnGUI()
    {
        if (!showDiagnostics) return;
        
        if (guiStyle == null)
        {
            guiStyle = new GUIStyle(GUI.skin.box);
            guiStyle.fontSize = fontSize;
            guiStyle.alignment = TextAnchor.UpperLeft;
            guiStyle.normal.textColor = Color.white;
            guiStyle.normal.background = MakeTexture(2, 2, new Color(0, 0, 0, 0.7f));
            guiStyle.padding = new RectOffset(10, 10, 10, 10);
        }
        
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("=== TERRAIN OPTIMIZATION DIAGNOSTICS ===");
        sb.AppendLine($"Press {toggleKey} to toggle | Update: {updateInterval}s");
        sb.AppendLine();
        
        // FPS Section
        sb.AppendLine("--- PERFORMANCE ---");
        sb.AppendLine($"FPS: {avgFPS:F1} (min: {minFPS:F1}, max: {maxFPS:F1})");
        
        Color fpsColor = GetFPSColor(avgFPS);
        string fpsStatus = avgFPS >= 55 ? "EXCELLENT" : avgFPS >= 45 ? "GOOD" : avgFPS >= 30 ? "POOR" : "CRITICAL";
        sb.AppendLine($"Status: {fpsStatus}");
        sb.AppendLine();
        
        // Save System
        sb.AppendLine("--- SAVE SYSTEM ---");
        sb.AppendLine($"Format: {SaveSystem.GetSaveFormat()}");
        sb.AppendLine($"Pending Saves: {SaveSystem.GetPendingSaveCount()}");
        
        if (SaveSystem.GetSaveFormat() != SaveSystem.SaveFormat.BinaryCompressed)
        {
            sb.AppendLine("⚠ WARNING: Not using BinaryCompressed!");
        }
        sb.AppendLine();
        
        // World System
        if (World.Instance != null)
        {
            sb.AppendLine("--- WORLD SYSTEM ---");
            sb.AppendLine($"Loaded Chunks: {World.Instance.GetLoadedChunkCount()}");
            sb.AppendLine($"Loaded/sec: +{chunksLoadedLastSecond} -{chunksUnloadedLastSecond}");
            
            var config = World.Instance.Config;
            if (config != null)
            {
                sb.AppendLine($"Load Radius: {config.LoadRadius} ({config.LoadRadius * config.chunkSize}m)");
                sb.AppendLine($"Unload Radius: {config.UnloadRadius}");
                sb.AppendLine($"Chunks/Frame: {config.ChunksPerFrame}");
                sb.AppendLine($"Target FPS: {config.TargetFPS}");
            }
            sb.AppendLine();
        }
        
        // Chunk Pool
        if (ChunkPoolManager.Instance != null)
        {
            sb.AppendLine("--- CHUNK POOL ---");
            int available = ChunkPoolManager.Instance.GetAvailableCount();
            sb.AppendLine($"Available: {available}");
            
            if (available < 10)
            {
                sb.AppendLine("⚠ WARNING: Pool running low!");
            }
            sb.AppendLine();
        }
        
        // Chunk Operations Queue
        if (ChunkOperationsQueue.Instance != null)
        {
            sb.AppendLine("--- OPERATIONS QUEUE ---");
            int pendingOps = ChunkOperationsQueue.Instance.GetPendingOperationsCount();
            sb.AppendLine($"Pending Ops: {pendingOps}");
            
            if (pendingOps > 100)
            {
                sb.AppendLine("⚠ WARNING: Large operation backlog!");
            }
            sb.AppendLine();
        }
        
        // Mesh Data Pool
        if (MeshDataPool.Instance != null)
        {
            sb.AppendLine("--- MESH DATA POOL ---");
            long memoryUsage = MeshDataPool.Instance.GetCurrentMemoryUsage();
            long maxMemory = World.Instance?.Config?.MaxMeshCacheSize ?? 0;
            float memoryPercent = maxMemory > 0 ? (float)memoryUsage / maxMemory * 100f : 0f;
            
            sb.AppendLine($"Memory: {FormatBytes(memoryUsage)} / {FormatBytes(maxMemory)}");
            sb.AppendLine($"Usage: {memoryPercent:F1}%");
            
            if (memoryPercent > 80f)
            {
                sb.AppendLine("⚠ WARNING: High memory pressure!");
            }
            sb.AppendLine();
        }
        
        // Terrain Analysis Cache
        sb.AppendLine("--- TERRAIN CACHE ---");
        int pendingAnalysis = TerrainAnalysisCache.GetPendingSaveCount();
        sb.AppendLine($"Pending Analysis: {pendingAnalysis}");
        
        if (pendingAnalysis > 50)
        {
            sb.AppendLine("⚠ WARNING: Cache backlog!");
        }
        sb.AppendLine();
        
        // LOD System (removed - incompatible with marching cubes)
        sb.AppendLine("--- LOD SYSTEM ---");
        sb.AppendLine("LOD System: Removed (incompatible with marching cubes)");
        sb.AppendLine();
        
        // Recommendations
        sb.AppendLine("--- RECOMMENDATIONS ---");
        List<string> recommendations = GetRecommendations();
        if (recommendations.Count == 0)
        {
            sb.AppendLine("✓ All systems optimal!");
        }
        else
        {
            foreach (var rec in recommendations)
            {
                sb.AppendLine($"• {rec}");
            }
        }
        
        // Display
        GUI.Box(new Rect(10, 10, 450, Screen.height - 20), sb.ToString(), guiStyle);
    }
    
    private List<string> GetRecommendations()
    {
        List<string> recs = new List<string>();
        
        // Check FPS
        if (avgFPS < 30)
        {
            recs.Add("Critical FPS! Reduce LoadRadius or enable LOD");
        }
        else if (avgFPS < 45)
        {
            recs.Add("Low FPS. Consider enabling LOD or reducing LoadRadius");
        }
        
        // Check save format
        if (SaveSystem.GetSaveFormat() != SaveSystem.SaveFormat.BinaryCompressed)
        {
            recs.Add("Enable BinaryCompressed format in TerrainConfigs");
        }
        
        // Check chunk pool
        if (ChunkPoolManager.Instance != null)
        {
            int available = ChunkPoolManager.Instance.GetAvailableCount();
            if (available < 10)
            {
                recs.Add("Increase chunk pool size (running low)");
            }
        }
        
        // Check operation backlog
        if (ChunkOperationsQueue.Instance != null)
        {
            int pending = ChunkOperationsQueue.Instance.GetPendingOperationsCount();
            if (pending > 100)
            {
                recs.Add("Reduce ChunksPerFrame or increase processing budget");
            }
        }
        
        // Check memory pressure
        if (MeshDataPool.Instance != null && World.Instance?.Config != null)
        {
            long memoryUsage = MeshDataPool.Instance.GetCurrentMemoryUsage();
            long maxMemory = World.Instance.Config.MaxMeshCacheSize;
            float memoryPercent = (float)memoryUsage / maxMemory * 100f;
            
            if (memoryPercent > 80f)
            {
                recs.Add("High memory usage. Reduce LoadRadius or enable LOD");
            }
        }
        
        // LOD removed - no longer recommended
        
        return recs;
    }
    
    private Color GetFPSColor(float fps)
    {
        if (fps >= 55) return Color.green;
        if (fps >= 45) return Color.yellow;
        if (fps >= 30) return new Color(1f, 0.5f, 0f); // Orange
        return Color.red;
    }
    
    private string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
        return $"{bytes / (1024f * 1024f * 1024f):F1} GB";
    }
    
    private Texture2D MakeTexture(int width, int height, Color color)
    {
        Color[] pixels = new Color[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = color;
        }
        
        Texture2D texture = new Texture2D(width, height);
        texture.SetPixels(pixels);
        texture.Apply();
        return texture;
    }
}


