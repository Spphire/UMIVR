using UnityEditor;
using UnityEditor.UI;
using UnityEngine;
using UnityEngine.UI;

[CustomEditor(typeof(DeleteButtonConfirm))]
[CanEditMultipleObjects]
public class DeleteButtonConfirmEditor : ButtonEditor
{
    public override void OnInspectorGUI()
    {
        // 先画 Button 自带的 Inspector
        base.OnInspectorGUI();

        // 获取目标脚本
        DeleteButtonConfirm btn = (DeleteButtonConfirm)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Delete Button Confirm Settings", EditorStyles.boldLabel);

        // 绘制自定义字段
        btn.buttonText = (TMPro.TextMeshProUGUI)EditorGUILayout.ObjectField("Button Text", btn.buttonText, typeof(TMPro.TextMeshProUGUI), true);
        btn.resetTimeThreshold =  EditorGUILayout.FloatField("Reset Time Threshold", btn.resetTimeThreshold);
        // 如果你希望修改自动保存
        if (GUI.changed)
        {
            EditorUtility.SetDirty(btn);
        }
    }
}