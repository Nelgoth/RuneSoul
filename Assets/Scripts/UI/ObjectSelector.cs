using UnityEngine;
using UnityEngine.UI;

public class ObjectSelector : MonoBehaviour
{
    public LayerMask selectableLayers;
    public GameObject highlightImagePrefab;
    public GameObject selectedObject;
    private Image highlightImage;
    public InventoryManager[] inventoryManager;

    private void Update(){
        if (selectedObject != null)
            UpdateHighlightPosition();
    }

    public void SelectionActivater(PlayerController caller){
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out var hit, Mathf.Infinity, selectableLayers)){
            if (hit.collider.gameObject == caller.gameObject) return;
            selectedObject = hit.collider.gameObject;
            SelectObject(selectedObject);
            inventoryManager = selectedObject.GetComponentsInChildren<InventoryManager>(true);
            for(int i = 0; i < inventoryManager.Length; i++){
                if (inventoryManager[i] != null && !inventoryManager[i].invToggle && !inventoryManager[i].internalOnly){
                //caller.inventory.OpenInv();
                    inventoryManager[i].OpenInv();
                }
            }
        }
    }
    private void SelectObject(GameObject obj) {
        if (highlightImage == null) {
            // Create the highlight image from the prefab
            GameObject highlightObj = Instantiate(highlightImagePrefab, obj.transform.position, Quaternion.identity);
            highlightImage = highlightObj.GetComponent<Image>();
            highlightImage.transform.SetParent(transform);
        }

        // Set the highlight image active and position it around the selected object
        highlightImage.gameObject.SetActive(true);
        highlightImage.transform.position = obj.transform.position;

        // Adjust the size of the highlight image based on the selected object's bounds
        Bounds bounds = CalculateObjectBounds(obj);
        Vector3 scale = new Vector3(bounds.size.x, bounds.size.y, 1f);
        highlightImage.transform.localScale = scale;
    }

    public void ClearSelection(PlayerController caller) {
        if (highlightImage != null) {
            Destroy(highlightImage.gameObject);
            highlightImage = null;
        }
    }
    private void UpdateHighlightPosition() {
        if (highlightImage != null && selectedObject != null) {
            // Update the position of the highlight image to match the selected object's position
            highlightImage.transform.position = selectedObject.transform.position;
        }
    }
    private Bounds CalculateObjectBounds(GameObject obj) {
        Renderer renderer = obj.GetComponent<Renderer>();
        if (renderer != null) {
            return renderer.bounds;
        }

        var col3d = obj.GetComponent<Collider>();
        if (col3d != null) return col3d.bounds;

        return new Bounds(obj.transform.position, Vector3.zero);
    }
}
