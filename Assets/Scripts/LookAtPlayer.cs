using System;
using UnityEngine;

public class LookAtPlayer : MonoBehaviour
{
    [Header("子节点 Transform（用于微调 LookAt 的部位）")]
    public Transform lookAtTargetBone;

    [Header("玩家 Transform")]
    public Transform playerTransform;

    private void Start()
    {
        if (playerTransform == null)
        {
            playerTransform = Camera.main.transform;
        }
    }

    void LateUpdate()
    {
        if (lookAtTargetBone == null || playerTransform == null)
            return;

        // 直接让父节点旋转，使子节点面向玩家
        transform.rotation = Quaternion.LookRotation(transform.position-playerTransform.position,Vector3.up);
    }
}