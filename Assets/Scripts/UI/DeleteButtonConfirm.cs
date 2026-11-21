using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class DeleteButtonConfirm : Button
{
    public TextMeshProUGUI buttonText;
    
    private bool firstClick = false;

    private float timer = 0;

    public float resetTimeThreshold;

    public bool clickable = true;
    private void Update()
    {
        if (clickable && firstClick)
        {
            timer += Time.deltaTime;
            if (timer >= resetTimeThreshold)
            {
                clickable = false;
                StartCoroutine(delayActive(0.1f));
                timer = 0;
                firstClick = false;
                targetGraphic.color = new Color(0, 0, 0, 1);
                buttonText.text = "delete";
            }
        }
    }

    // 重写点击处理
    public override void OnPointerClick(PointerEventData eventData)
    {
        if (!IsActive() || !IsInteractable() || !clickable)
            return;
        clickable = false;
        StartCoroutine(delayActive(0.1f));
        if(!firstClick)
        {
            firstClick = true;
            targetGraphic.color = new Color(1, 0, 0, 1);
            buttonText.text = "confirm";
        }
        else
        {
            firstClick = false;
            timer = 0;
            UISystemProfilerApi.AddMarker("Button.onClick", this);
            onClick.Invoke();
            targetGraphic.color = new Color(0, 0, 0, 1);
            buttonText.text = "delete";
        }
    }

    public IEnumerator delayActive(float delay)
    {
        yield return new WaitForSecondsRealtime(delay);
        clickable = true;
    }
}
