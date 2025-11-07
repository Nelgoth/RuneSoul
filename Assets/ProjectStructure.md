# Rune Soul Project Structure Documentation

## Overview
Rune Soul is a voxel-based game with procedurally generated terrain that supports multiplayer via Unity Netcode for GameObjects.

## World Seed Management (CRITICAL)
Each world has a unique seed stored in `WorldMetadata.WorldSeed` that determines its terrain generation:

### Flow of Seed Data
1. **World Creation** (`GameUIManager.CreateNewWorld`):
   - User enters a seed in UI or one is randomly generated
   - Seed is passed to `WorldSaveManager.InitializeWorld(worldName, isMultiplayer, seed)`
   
2. **World Storage** (`WorldSaveManager`):
   - Seed is stored in `WorldMetadata.WorldSeed` (persisted to disk)
   - Each world has its own unique WorldId and metadata file
   
3. **World Loading** (`World.InitializeWorld`):
   - `WorldSaveManager.GetWorldMetadata()` retrieves the stored seed
   - Seed is applied to `TerrainConfigs.noiseSeed` BEFORE any terrain generation
   - `TerrainAnalysisCache.ResetCache()` clears stale cache data
   
4. **Terrain Generation** (`Chunk.cs`, `DensityFieldGenerationJob`):
   - Uses `World.Instance.noiseSeed` (which now reflects WorldMetadata.WorldSeed)
   - All noise generation uses this seed for consistent, deterministic terrain

### Important Rules
- **NEVER use PlayerPrefs for seed storage** - it's global and causes cross-world pollution
- **ALWAYS apply WorldSeed to TerrainConfigs.noiseSeed** before terrain generation
- **TerrainConfigs.noiseSeed is now a runtime property** - the serialized `defaultNoiseSeed` is only a fallback
- **TerrainAnalysisCache must be cleared** when loading a new world to prevent stale data
- **Generation Hash includes both WorldSeed and TerrainConfig parameters** for cache validation

## Key Components

### Terrain Generation
- `World.cs`: Central manager for terrain generation and chunk management
  - Uses lowercase property names (`chunkSize`, `voxelSize`, `surfaceLevel`)
  - Uses `operationsQueue` for handling chunk operations
  - Provides `RegisterChunk(chunkCoord, chunk)` method instead of `AddChunk`
  - Method `Generate()` is used to trigger mesh generation

### Chunk System
- `Chunk.cs`: Represents a section of the terrain
  - Uses `CompleteAllJobs()` to ensure all jobs have finished
  - Uses `UpdateFromSerializedData(densityValues, voxelStates, voxelHitpoints)` to apply data
  - Does NOT support `LoadFromChunkData` or `ScheduleMeshUpdate` methods

### Chunk Data
- `ChunkData.cs`: Stores the actual voxel and density data
  - Properties like `serializedDensityValues` are lowercase
  - `hasModifiedData` is a private field, must use `SaveData()` to mark as modified
  - Provides `LoadFromSerialization()` method
  - Provides `SetVoxel()` and `SetDensityPoint()` for updating data

### Networking
- **TerrainNetworkManager System** (New):
  - `TerrainNetworkManager.cs`: Unified manager for terrain synchronization
    - Handles chunk ownership, modification requests, and verification
    - Uses a direct RPC-based approach for terrain modifications
    - Supports hash-based verification to ensure client-server consistency
    - Prioritizes chunk synchronization based on player proximity
  
  - `TerrainNetworkManagerInstaller.cs`: Handles automatic creation and registration
    - Ensures a single TerrainNetworkManager instance exists
    - Connects to NetworkManager events to handle startup/shutdown

  - `TerrainNetworkClient.cs`: Client-side API for terrain interactions
    - Provides methods for requesting terrain modifications
    - Handles requesting chunk data and ownership
    - Supports batched operations and position updates
    
  - `PlayerTerrainInteraction.cs`: Player input handler for terrain modification
    - Translates player input into network requests
    - Handles voxel placement and removal with proper coordinate conversion
    - Updates player position for chunk prioritization

- **Legacy System** (Deprecated):
  - `TerrainSyncManager.cs`: Legacy manager for terrain synchronization
  - `ClientChunkReceiver.cs`: Legacy handler for client-side chunk data
  - `NetworkHandlerRegistration.cs`: Legacy network prefab management
  - `CustomNetworkHandler.cs`: Legacy network event handling

### Operation Queue
- `ChunkOperationsQueue.cs`: Manages operations on chunks
  - Uses `QueueChunkForLoad()` instead of `QueueOperation()`
  - Uses `HasPendingLoadOperation()` to check if a chunk is being loaded

## Common Patterns

### Chunk Coordinate System
- Uses `Vector3Int` for chunk coordinates
- `Coord` utility class handles conversions between world and chunk space

### Chunk State Management
- `ChunkStateManager.Instance.TryChangeState()` is used to update chunk states
- States include: None, Loading, Loaded, Modified, Saved

### Data Serialization
- Serialized arrays store data for network transfer and saving
- Data must be explicitly saved with `SaveData()` method
- ChunkData uses `serializedDensityValues`, `serializedVoxelStates`, and `serializedVoxelHitpoints` properties
- Network batch sizes limited to ~1024 elements to prevent packet size issues

### Terrain Modification
- Modified chunks are tracked for network sync
- World.Instance.NotifyChunkModified() is called when a chunk is modified

## Network Synchronization Process

### New System
1. TerrainNetworkManagerInstaller sets up the TerrainNetworkManager on game start
2. World connects to the TerrainNetworkManager in Start()
3. Players use PlayerTerrainInteraction to request terrain modifications
4. TerrainNetworkManager processes requests and broadcasts changes
5. Ownership system allows temporary client ownership for batched modifications
6. Hash-based verification ensures consistency between client and server

### Legacy System (Deprecated)
1. Server sets up TerrainSyncManager
2. Clients connect and receive world settings
3. Server sends modified chunks to clients
4. Clients apply received data and generate meshes

## Performance Optimization
- **Batch Processing**: Large data arrays are sent in smaller batches (1024 elements per batch)
- **Frame Distribution**: Processing is spread across multiple frames to prevent FPS drops
- **Prioritization**: Chunks nearest to players are prioritized for synchronization
- **Timeout Handling**: Automatic retry for timed-out chunk transfers
- **Method Separation**: Logic separated into discrete methods to avoid try/catch with yield issues
- **Ownership Model**: Temporary chunk ownership for efficient batched modifications
- **Hash Verification**: Periodic hash checks to ensure client-server consistency
- **On-Demand Synchronization**: Clients request specific chunk data when needed

## Avoiding Common Errors

### Try/Catch with Yield
- **NEVER** use try/catch blocks with yield statements (causes CS1626 error)
- Instead, separate into multiple methods:
  ```csharp
  // INCORRECT:
  private IEnumerator ProcessWithTryCatch() {
      try {
          yield return null; // ERROR: Cannot yield in try block with catch
      } catch (Exception e) {
          Debug.LogError(e);
      }
  }
  
  // CORRECT:
  private IEnumerator ProcessSafely() {
      DoRiskyOperation(); // Separate method that might throw
      yield return null;  // Safe to yield outside try/catch
  }
  
  private void DoRiskyOperation() {
      try {
          // Code that might throw exceptions
      } catch (Exception e) {
          Debug.LogError(e);
      }
  }
  ```

### Property Access
- Native array properties cannot be modified directly (use setter methods)
- APIs are case-sensitive 
- ChunkData properties use the `serialized` prefix (`serializedDensityValues` not `Density`)

### Network Optimization
- Avoid sending full chunk data in one network message
- Use batched transfers with acknowledgment
- Prioritize modified chunks and chunks near players
- Implement timeout and retry mechanisms 

## Network Prefab Handling
- **Template vs. Clones**: Network prefabs like `CustomNetworkHandlerPrefab` and `TerrainSyncManagerPrefab` should be treated as templates
  - On the server, these are spawned using `NetworkObject.Spawn()` which creates networked clones
  - On clients, the original template prefabs should remain inactive
  - Only the cloned instances (with "(Clone)" in their name) should be active on clients
- **Proper Instantiation Pattern**:
  ```csharp
  // Only spawn on the server:
  if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) {
      NetworkObject netObj = prefab.GetComponent<NetworkObject>();
      if (netObj != null && !netObj.IsSpawned) {
          netObj.Spawn(); // Creates clones on all clients
      }
  }
  
  // On clients, look for clones:
  if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient) {
      var clones = FindObjectsOfType<YourComponent>(true)
          .Where(c => c.gameObject.name.Contains("Clone")).ToArray();
      if (clones.Length > 0) {
          // Use clone, not the template
      }
  }
  ```
- **Common Issues**:
  - Activating template prefabs on both server and client leads to synchronization issues
  - When templates are active on both sides, network IDs can be mismatched
  - Always check for cloned instances on clients before activating templates 