using UnityEngine;
using UnityEngine.UI;

[ExecuteAlways]
public class ScaleEditor : MonoBehaviour
{
    public RectTransform UnityCanvas;
    public RectTransform BG;
    public RectTransform Menu;

    
    
    public float width=400f;
    public float height=400f;
    
    public GridLayoutGroup GridLG;
    public float gridHeight=50f;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        UnityCanvas.sizeDelta = new Vector2(width, height);
        UnityCanvas.localPosition = new Vector3(0,(height-400)*UnityCanvas.localScale.y+0.35f,0);
        BG.sizeDelta = new Vector2(width, height);
        Menu.sizeDelta = new Vector2(0f, height);

        if (GridLG != null)
        {
            float paddingHorizon = Menu.gameObject.GetComponent<LayoutGroup>().padding.horizontal;
            GridLG.cellSize = new Vector2((width-paddingHorizon-GridLG.spacing.x)/2, gridHeight);
        }
    }
}
