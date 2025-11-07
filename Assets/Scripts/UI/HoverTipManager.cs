using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using NelsUtils;
using UnityEngine.Rendering;
using UnityEngine.InputSystem;

public class HoverTipManager : MonoBehaviour{
    public TextMeshProUGUI tipText;
    public RectTransform tipWindow;
    public static Action<string> OnMouseHover;
    public static Action OnMouseLoseFocus;
    private void OnEnable(){
        OnMouseHover += ShowTip;
        OnMouseLoseFocus += HideTip;
    }

    private void OnDisable(){
        OnMouseHover -= ShowTip;
        OnMouseLoseFocus -= HideTip;
    }
    
    private void Start(){
        EnsureRefs();
        HideTip();
    }

    private void EnsureRefs(){
		if (tipText != null && tipWindow != null) return;
		var canvas = GetComponentInParent<Canvas>();
		if (canvas == null) return;
		if (tipText == null) tipText = canvas.GetComponentInChildren<TextMeshProUGUI>(true);
		if (tipWindow == null){
			var found = canvas.transform.Find("TipWindow") as RectTransform;
			if (found == null && tipText != null) found = tipText.transform.parent as RectTransform;
			tipWindow = found;
		}
    }
    
	private void ShowTip(string tip){
		EnsureRefs();
		if (tipText == null || tipWindow == null) return;
		tipText.text = tip;
		var canvas = tipWindow.GetComponentInParent<Canvas>();
		if (canvas == null) return;
		var canvasRect = canvas.transform as RectTransform;
		float maxWidth = 200f;
		float width = tipText.preferredWidth > maxWidth ? maxWidth : tipText.preferredWidth;
		tipWindow.sizeDelta = new Vector2(width, tipText.preferredHeight);
		// Normalize anchors/pivot so anchoredPosition math is predictable
		tipWindow.anchorMin = new Vector2(.5f, .5f);
		tipWindow.anchorMax = new Vector2(.5f, .5f);
		tipWindow.pivot = new Vector2(0f, 1f); // top-left pivot feels natural for tooltips
		Vector2 screenPos = Pointer.current != null ? Pointer.current.position.ReadValue() : Vector2.zero;
		Vector2 localPoint;
		Camera uiCamera = null;
		if (canvas.renderMode == RenderMode.ScreenSpaceOverlay){
			uiCamera = null;
		}
		else{
			uiCamera = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
		}
		// Convert relative to root canvas to avoid compounded parent offsets
		if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screenPos, uiCamera, out localPoint)){
			Vector2 offset = new Vector2(12f, -12f);
			Vector2 anchored = localPoint + offset;
			// Clamp inside canvas rect to avoid off-screen placement
			var pr = canvasRect.rect;
			Vector2 minBounds = new Vector2(pr.xMin + tipWindow.sizeDelta.x * tipWindow.pivot.x, pr.yMin + tipWindow.sizeDelta.y * tipWindow.pivot.y);
			Vector2 maxBounds = new Vector2(pr.xMax - tipWindow.sizeDelta.x * (1 - tipWindow.pivot.x), pr.yMax - tipWindow.sizeDelta.y * (1 - tipWindow.pivot.y));
			anchored.x = Mathf.Clamp(anchored.x, minBounds.x, maxBounds.x);
			anchored.y = Mathf.Clamp(anchored.y, minBounds.y, maxBounds.y);
			tipWindow.anchoredPosition = anchored;
		}
		tipWindow.gameObject.SetActive(true);
	}
    public void HideTip(){
		EnsureRefs();
		if (tipText != null) tipText.text = default;
		if (tipWindow != null) tipWindow.gameObject.SetActive(false);
    }
}
