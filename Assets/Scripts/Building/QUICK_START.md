# Building System Quick Start

## Minimum Setup (5 minutes)

### Step 1: Create a Simple Building Piece Prefab

1. Create a cube GameObject (GameObject → 3D Object → Cube)
2. Name it "WallPiece"
3. Add these components:
   - `BuildingPieceInstance`
   - `NetworkObject`
   - `BuildingStructuralIntegrity`
4. Add a `BuildingSnapPoint` as a child:
   - Position it at (0, 0, 0.5) - front face
   - Set snap radius to 0.3
5. Add a BoxCollider (if not already present)
6. Drag to Project window to create prefab

### Step 2: Create BuildingPiece ScriptableObject

1. Right-click in Project → Create → Building → Building Piece
2. Name it "WallPiece"
3. Assign your prefab
4. Set snap points: Add one at (0, 0, 0.5)
5. Create preview materials (or use defaults)

### Step 3: Add BuildingSystem to Player

1. Select your player GameObject
2. Add Component → `BuildingSystem`
3. Drag your BuildingPiece ScriptableObject to "Available Pieces" array
4. The system will auto-find camera and create preview

### Step 4: Test

1. Play the game
2. Press Tab to open build menu (if you have UI) or select piece programmatically
3. Press R to rotate
4. Left-click to place
5. Right-click to enter destroy mode

## Creating a Build Menu UI (Optional)

1. Create Canvas (GameObject → UI → Canvas)
2. Create Panel child (right-click Canvas → UI → Panel)
3. Add `BuildingMenuUI` component to Panel
4. Create Button prefab:
   - GameObject → UI → Button
   - Add Image child for icon
   - Add TextMeshProUGUI child for name
5. Assign in BuildingMenuUI:
   - Menu Panel: Your panel
   - Piece Button Container: Empty GameObject child of panel
   - Piece Button Prefab: Your button prefab

## Common Building Piece Configurations

### Wall Piece
- Snap points: Front/back faces, corners
- Can support others: Yes
- Requires foundation: No

### Floor Piece
- Snap points: All edges
- Can support others: Yes
- Requires foundation: Yes (or on walls)

### Roof Piece
- Snap points: Bottom edges
- Can support others: No
- Requires foundation: Yes (on walls/floors)

### Foundation Piece
- Snap points: Top face
- Can support others: Yes
- Requires foundation: Yes (on ground)

## Tips

- Start with simple pieces (walls/floors) before complex ones
- Test snapping with 2-3 pieces first
- Adjust snap distance if pieces don't connect properly
- Use different snap types for different connection types
- Structural integrity colors help debug support issues



