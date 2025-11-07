using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface IStatus {
    public float Health { set; get; }
    public float Stamina { set; get; }
    public float Mental { set; get; }
    public float Hunger { set; get; }
    public float Thirst { set; get; }
    public void Damage(UnitController caller, float damage, string toolType);
    public void Damage(float damage, string toolType);
    public void UseStat(float value, string statToUse);
    //public float StatGet(string statToGet);
    //public void StatUse(float amount, string statToUse);
}
