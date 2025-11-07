using System;

[System.Serializable]
public class WorldMetadata
{
    public string WorldId;
    public string WorldName;
    public bool IsMultiplayerWorld;
    public DateTime CreatedDate;
    public DateTime LastPlayed;
    public int WorldSeed;  // Added seed field
}