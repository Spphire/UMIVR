using System;
using UnityEngine;

public static class ULog
{
#if !DISABLE_ALL_DEBUG
    public static void Log(string msg) => Debug.Log(msg);
    public static void LogWarning(string msg) => Debug.LogWarning(msg);
#else
    // DISABLE_ALL_LOG 被定义后，Log / Warn 什么都不做
    public static void Log(string msg) { }
    public static void LogWarning(string msg) { }
#endif

    // Error 通常保留
    public static void LogError(string msg) => Debug.LogError(msg);
    public static void LogError(Exception ex) => Debug.LogError(ex);
}