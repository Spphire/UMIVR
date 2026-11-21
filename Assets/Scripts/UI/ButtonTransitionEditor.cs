using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ButtonTransitionEditor : MonoBehaviour
{
    public List<Button> buttons2modify;
    public Color normalColor;
    public Color highlightedColor;
    public Color pressedColor;
    public Color selectedColor;
    public Color disabledColor;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        foreach (Button button in buttons2modify)
        {
            button.targetGraphic.color = Color.black;
            var cb = button.colors;
            cb.normalColor = normalColor;
            cb.highlightedColor = highlightedColor;
            cb.pressedColor = pressedColor;
            cb.selectedColor = selectedColor;
            cb.disabledColor = disabledColor;
            button.colors = cb;
        }
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
