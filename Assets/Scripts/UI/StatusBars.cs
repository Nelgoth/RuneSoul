using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class StatusBars : MonoBehaviour
{
    public Image healthFillImage;
    public Image staminaFillImage;
    public Image mentalFillImage;
    public Image hungerFillImage;
    public Image thirstFillImage;
    public GameObject chargeBar;
    public Image chargeFillImage;

    public void SetStat(float normalized, string statToSet)
    {
        if (statToSet == "Health"){
            healthFillImage.fillAmount = normalized;
        }
        if (statToSet == "Stamina"){
            staminaFillImage.fillAmount = normalized;
        }
        if (statToSet == "Mental"){
            mentalFillImage.fillAmount = normalized;
        }
        if (statToSet == "Hunger"){
            hungerFillImage.fillAmount = normalized;
        }
        if (statToSet == "Thirst"){
            thirstFillImage.fillAmount = normalized;
        }
        if (statToSet == "Charge"){
            chargeFillImage.fillAmount = normalized;
        }
    }    
}

