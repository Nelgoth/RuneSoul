# Valheim-Style Building System

A complete building system inspired by Valheim, featuring piece placement, snapping, structural integrity, and multiplayer support.

## Features

- **Building Piece Placement**: Place building pieces with preview/ghost rendering
- **Snapping System**: Pieces automatically snap to each other at connection points
- **Structural Integrity**: Pieces turn green/yellow/red based on support (like Valheim)
- **Rotation**: Rotate pieces before placing (default: R key)
- **Destruction Mode**: Right-click to destroy pieces
- **Build Menu**: Tab key to open/close build menu
- **Multiplayer Support**: Fully networked using Unity Netcode

## Setup Instructions

### 1. Create Building Pieces

1. Create a ScriptableObject for each building piece:
   - Right-click in Project window → Create → Building → Building Piece
   - Configure the piece:
     - Set the prefab (must have BuildingPieceInstance component)
     - Set snap points (local positions)
     - Configure structural properties
     - Set materials for preview (green/red for valid/invalid)

2. Create prefabs for building pieces:
   - Each prefab must have:
     - `BuildingPieceInstance` component
     - `NetworkObject` component (for multiplayer)
     - `BuildingStructuralIntegrity` component (optional but recommended)
     - `BuildingSnapPoint` components as children (for snapping)
     - Colliders (for placement validation)

### 2. Setup Building System on Player

1. Add `BuildingSystem` component to your player GameObject
2. Assign references:
   - Player Camera (will auto-find Camera.main if not set)
   - Building Preview (will auto-create if not set)
   - Building Menu UI (optional, but recommended)
   - Available Pieces array (drag your BuildingPiece ScriptableObjects here)

3. Configure settings:
   - Placement Range: How far player can place pieces
   - Snap Distance: How close pieces need to be to snap
   - Placement Layer Mask: What layers pieces can be placed on

### 3. Setup Building Menu UI

1. Create a UI Canvas (if you don't have one)
2. Create a panel for the build menu
3. Add `BuildingMenuUI` component to the panel
4. Create a button prefab for piece selection:
   - Should have a Button component
   - Should have an Image for the icon
   - Should have TextMeshProUGUI for the name
5. Assign references in BuildingMenuUI:
   - Menu Panel
   - Piece Button Container (parent for buttons)
   - Piece Button Prefab
   - Selected Piece Name/Description/Icon UI elements

### 4. Setup Building Piece Prefabs

For each building piece prefab:

1. Add `BuildingPieceInstance` component
2. Add `NetworkObject` component
3. Add `BuildingStructuralIntegrity` component (optional)
4. Add `BuildingSnapPoint` components as children:
   - Position them at connection points
   - Set snap radius
   - Configure snap type if needed
5. Add colliders (for placement validation)
6. Assign the prefab to the BuildingPiece ScriptableObject

### 5. Configure Snap Points

1. Add `BuildingSnapPoint` components as children of your building piece prefabs
2. Position them at connection points (e.g., corners, edges)
3. Set the snap radius (how close pieces need to be)
4. Configure snap type if you want specific connections (e.g., "wall", "floor")

### 6. Setup Structural Integrity (Optional)

1. Add `BuildingStructuralIntegrity` component to building piece prefabs
2. Configure:
   - Max Integrity: Maximum structural integrity value
   - Foundation Integrity: Integrity when on ground/foundation
   - Support Decay Rate: How much integrity is lost per piece away from foundation
   - Colors: Green/yellow/red for well/moderately/poorly supported
3. Assign renderers that should change color based on support

### 7. Create Preview Materials

Create materials for the preview system:
- **Preview Material**: Transparent material for ghost preview
- **Valid Placement Material**: Green material (when placement is valid)
- **Invalid Placement Material**: Red material (when placement is invalid)

Assign these to your BuildingPiece ScriptableObjects.

## Controls

- **Tab**: Open/close build menu
- **R**: Rotate selected piece (when in build mode)
- **Left Click**: Place piece (when in build mode)
- **Right Click**: Toggle destroy mode
- **Left Click**: Destroy piece (when in destroy mode)
- **Escape**: Exit building mode

## Usage Example

```csharp
// Get the building system
BuildingSystem buildingSystem = GetComponent<BuildingSystem>();

// Programmatically select a piece
BuildingPiece wallPiece = // ... get your piece
buildingSystem.SelectPiece(wallPiece);
```

## Network Setup

The building system uses Unity Netcode for multiplayer:

1. Ensure your NetworkManager is set up
2. Building pieces are automatically networked when placed
3. Only the server can spawn pieces (via ServerRpc)
4. All clients see placed pieces automatically

## Customization

### Adding Custom Building Pieces

1. Create a new BuildingPiece ScriptableObject
2. Create a prefab with required components
3. Add it to the Available Pieces array in BuildingSystem

### Custom Snap Types

Set the `snapType` on BuildingSnapPoint components to create specific connection types (e.g., "wall", "floor", "roof").

### Custom Structural Integrity

Modify `BuildingStructuralIntegrity` to change how support is calculated. You can add custom rules for different piece types.

## Troubleshooting

**Pieces don't snap:**
- Check that snap points are positioned correctly
- Verify snap distance is appropriate
- Ensure BuildingSnapPoint components are on the prefabs

**Pieces don't show preview:**
- Check that preview materials are assigned
- Verify camera reference is set
- Check placement range settings

**Pieces don't network:**
- Ensure NetworkObject component is on prefabs
- Check that NetworkManager is running
- Verify ServerRpc is being called correctly

**Structural integrity doesn't work:**
- Ensure BuildingStructuralIntegrity component is on prefabs
- Check that renderers are assigned
- Verify materials have _Color property

## Notes

- Building pieces must be spawned server-side for multiplayer
- Structural integrity calculations run periodically (every 30 frames)
- Preview system creates temporary instances that are destroyed when not in use
- Snap points are marked as occupied when pieces connect



