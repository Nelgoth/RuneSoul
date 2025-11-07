using UnityEngine;
using UnityEngine.EventSystems;

public class UIDragHandler : MonoBehaviour, IDragHandler, IBeginDragHandler
{
    public RectTransform dragRectTransform;
    public RectTransform draggableArea;
    private bool isDragging = false;
    private Vector2 dragOffset;
    private Vector2 dragStartPosition;

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (!RectTransformUtility.RectangleContainsScreenPoint(draggableArea, eventData.position))
        {
            isDragging = false;
            return;
        }

        isDragging = true;
        dragStartPosition = dragRectTransform.position;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(draggableArea, eventData.position, eventData.pressEventCamera, out dragOffset);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!isDragging)
            return;

        Vector2 localCursorPosition;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(draggableArea, eventData.position, eventData.pressEventCamera, out localCursorPosition);

        Vector2 newPosition = dragStartPosition + (localCursorPosition - dragOffset);
        dragRectTransform.position = newPosition;
    }
}
