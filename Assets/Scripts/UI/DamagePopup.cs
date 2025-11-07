using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using NelsUtils;

public class DamagePopup : MonoBehaviour
{
    public static DamagePopup Create(Vector3 position, float damageAmount, bool isCriticalHit){
        Transform damagePopupTransform = Instantiate(GameAssets.i.PopUp, position, Quaternion.identity);
        DamagePopup damagePopup = damagePopupTransform.GetComponent<DamagePopup>();
        damagePopup.Setup(damageAmount, isCriticalHit);
        return damagePopup;
    }
    private static int sortingOrder;
    private const float DISAPPER_TIMER_MAX = .1f;
    private TextMeshPro textMesh;
    private float disappearTimer;
    private Color textColor;
    private Vector3 moveVector;

    private void Awake(){
        textMesh = transform.GetComponent<TextMeshPro>();
    }
    public void Setup(float damageAmount, bool isCriticalHit){
        textMesh.SetText(damageAmount.ToString());
        if (!isCriticalHit){
            textMesh.fontSize = 5f;
            textColor = Utils.GetColorFromString("FFC500");
        }
        else {
            textMesh.fontSize = 10f;
            textColor = Utils.GetColorFromString("FF2B00");
        }
        textMesh.color = textColor;
        disappearTimer =  DISAPPER_TIMER_MAX;
        sortingOrder++;
        textMesh.sortingOrder = sortingOrder;
        moveVector = new Vector3(.7f,1) * 3f;
    }

    private void Update(){
        transform.position += moveVector * Time.deltaTime;
        moveVector -= moveVector * 8f * Time.deltaTime;
        if (disappearTimer > DISAPPER_TIMER_MAX * .5f){
            float increaseScaleAmount = 1f;
            transform.localScale += Vector3.one * increaseScaleAmount * Time.deltaTime;
        }
        else {
            float decreaseScaleAmount = 1f;
            transform.localScale -= Vector3.one * decreaseScaleAmount * Time.deltaTime;
        }
        disappearTimer -= Time.deltaTime;
        if (disappearTimer < 0){
            float disappearSpeed = 3f;
            textColor.a -= disappearSpeed * Time.deltaTime;
            textMesh.color = textColor;
            if (textColor.a < 0){
                Destroy(gameObject);
            }
        }
    }
}
