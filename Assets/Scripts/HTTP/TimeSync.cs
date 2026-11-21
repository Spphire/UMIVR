using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using UnityEngine.UI;

public class TimeSync : MonoBehaviour
{
    [Header("UI")]
    public IPListManager ipListManager;

    public TextMeshProUGUI debugText;

    [Header("Sync Config")]
    [Tooltip("Only samples with RTT <= this threshold update the offset (milliseconds)")]
    public long acceptableRttMs = 30;

    [Tooltip("Per-request timeout target (milliseconds). Unity timeout is seconds, so this script also applies a manual timeout.")]
    public long requestTimeoutMs = 100;

    [Tooltip("Number of requests per sync (recommended 5~10)")]
    public int syncRequestCount = 5;

    [Tooltip("Interval between requests (seconds)")]
    public float requestInterval = 0.1f;

    [Tooltip("Continuous re-sync interval (seconds)")]
    public float loopInterval = 1f;

    [Tooltip("Start the background re-sync loop automatically after required references are ready")]
    public bool autoStartSyncLoop = true;

    [Tooltip("If the background loop heartbeat is older than this threshold, restart it (seconds)")]
    public float loopWatchdogTimeout = 3f;

    // Time offset after sync (server time - local time)
    private static long timeOffsetMs = 0;
    private static bool hasSynced = false;
    private Coroutine syncLoopCoroutine;
    private bool isSyncInProgress = false;
    private bool isSyncLoopRunning = false;
    private long lastSuccessfulRttMs = -1;
    private long lastSuccessfulOffsetMs = 0;
    private int consecutiveSkippedSyncCount = 0;
    private double lastLoopHeartbeatRealtime = -1.0;
    private double lastWatchdogCheckRealtime = -1.0;

    private enum SyncAttemptResult
    {
        Valid,
        TimedOut,
        RequestFailed,
        OverThreshold,
        ParseFailed
    }

    private void OnEnable()
    {
        EnsureSyncLoopRunning("OnEnable");
    }

    private void Update()
    {
        if (!autoStartSyncLoop)
            return;

        double now = Time.realtimeSinceStartupAsDouble;
        if (lastWatchdogCheckRealtime > 0.0 && now - lastWatchdogCheckRealtime < 1.0)
            return;

        lastWatchdogCheckRealtime = now;
        EnsureSyncLoopRunning("Watchdog");
    }

    private void OnApplicationPause(bool pause)
    {
        if (!pause)
            EnsureSyncLoopRunning("OnApplicationPause(false)");
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus)
            EnsureSyncLoopRunning("OnApplicationFocus(true)");
    }

    /// <summary>
    /// Button callback
    /// </summary>
    public void SyncTime()
    {
        if (!TryGetSyncUrl(out string url))
        {
            debugText.text = "URL is empty";
            return;
        }

        if (!isSyncInProgress)
            StartCoroutine(RunSyncOnce(url, true));
    }

    /// <summary>
    /// Continuous sync loop
    /// </summary>
    private IEnumerator SyncLoop()
    {
        isSyncLoopRunning = true;
        lastLoopHeartbeatRealtime = Time.realtimeSinceStartupAsDouble;

        while (true)
        {
            lastLoopHeartbeatRealtime = Time.realtimeSinceStartupAsDouble;
            if (TryGetSyncUrl(out string url) && !isSyncInProgress)
                yield return StartCoroutine(RunSyncOnce(url, true));

            lastLoopHeartbeatRealtime = Time.realtimeSinceStartupAsDouble;
            yield return new WaitForSeconds(loopInterval);
        }
    }

    private IEnumerator RunSyncOnce(string url, bool updateDebugText)
    {
        isSyncInProgress = true;
        yield return StartCoroutine(SyncTimeMultiple(url, updateDebugText));
        isSyncInProgress = false;
    }

    /// <summary>
    /// Multiple requests, select the one with minimum RTT
    /// </summary>
    private IEnumerator SyncTimeMultiple(string url, bool updateDebugText)
    {
        long bestRtt = long.MaxValue;
        long bestOffset = 0;
        long bestObservedRtt = long.MaxValue;
        bool foundValid = false;
        int timeoutCount = 0;
        int overThresholdCount = 0;
        int effectiveRequestCount = Math.Max(1, syncRequestCount);

        for (int i = 0; i < effectiveRequestCount; i++)
        {
            yield return StartCoroutine(
                SingleSyncRequest(url,
                    (result, rtt, offset, serverTimeResponse, localt0) =>
                    {
                        if (rtt > 0 && rtt < bestObservedRtt)
                            bestObservedRtt = rtt;

                        if (result == SyncAttemptResult.TimedOut)
                        {
                            timeoutCount++;
                            return;
                        }

                        if (result == SyncAttemptResult.OverThreshold)
                        {
                            overThresholdCount++;
                            return;
                        }

                        if (result != SyncAttemptResult.Valid)
                            return;

                        if (rtt < bestRtt)
                        {
                            bestRtt = rtt;
                            bestOffset = offset;
                            foundValid = true;
                        }
                    })
            );

            if (i + 1 < effectiveRequestCount)
                yield return new WaitForSeconds(requestInterval);
        }

        if (!foundValid)
        {
            consecutiveSkippedSyncCount++;
            if (updateDebugText && debugText != null)
            {
                debugText.text =
                    $"Sync skipped x{consecutiveSkippedSyncCount} | " +
                    $"{BuildRoundStatsText(bestObservedRtt, timeoutCount, overThresholdCount)} | " +
                    $"{BuildLastSuccessText()}";
            }
            yield break;
        }

        timeOffsetMs = bestOffset;
        hasSynced = true;
        lastSuccessfulRttMs = bestRtt;
        lastSuccessfulOffsetMs = bestOffset;
        consecutiveSkippedSyncCount = 0;

        if (updateDebugText && debugText != null)
        {
            debugText.text =
                $"{BuildLastSuccessText()} | " +
                $"{BuildRoundStatsText(bestObservedRtt, timeoutCount, overThresholdCount)}";
        }
    }

    /// <summary>
    /// Single time sync request
    /// </summary>
    private IEnumerator SingleSyncRequest(
        string url,
        Action<SyncAttemptResult, long, long, ServerTimeResponse, long> onResult
    )
    {
        // t0: client sends request
        long t0 = GetLocalTimestampMs();

        UnityWebRequest request = UnityWebRequest.Get(url);
        request.timeout = Mathf.Max(1, Mathf.CeilToInt(GetEffectiveRequestTimeoutMs() / 1000f));

        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
        double deadline = Time.realtimeSinceStartupAsDouble + GetEffectiveRequestTimeoutMs() / 1000.0;
        while (!operation.isDone && Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return null;
        }

        if (!operation.isDone)
        {
            request.Abort();
            onResult?.Invoke(SyncAttemptResult.TimedOut, -1, 0, null, t0);
            yield break;
        }

        // t1: client receives response
        long t1 = GetLocalTimestampMs();

        if (request.result != UnityWebRequest.Result.Success)
        {
            onResult?.Invoke(SyncAttemptResult.RequestFailed, -1, 0, null, t0);
            yield break;
        }

        long rtt = t1 - t0;

        if (rtt <= 0 || rtt > GetEffectiveAcceptableRttMs())
        {
            onResult?.Invoke(SyncAttemptResult.OverThreshold, rtt, 0, null, t0);
            yield break;
        }

        ServerTimeResponse serverTime;
        try
        {
            serverTime = JsonUtility.FromJson<ServerTimeResponse>(
                request.downloadHandler.text
            );
        }
        catch
        {
            onResult?.Invoke(SyncAttemptResult.ParseFailed, rtt, 0, null, t0);
            yield break;
        }

        if (serverTime == null)
        {
            onResult?.Invoke(SyncAttemptResult.ParseFailed, rtt, 0, null, t0);
            yield break;
        }

        // TryParse + seconds → milliseconds
        if (!TryParseServerTime(serverTime.t2, out long t2Ms) ||
            !TryParseServerTime(serverTime.t3, out long t3Ms))
        {
            onResult?.Invoke(SyncAttemptResult.ParseFailed, rtt, 0, null, t0);
            yield break;
        }

        // ---- NTP offset algorithm ----
        // offset = ((t2 - t0) + (t3 - t1)) / 2
        long offset = ((t2Ms - t0) + (t3Ms - t1)) / 2;

        onResult?.Invoke(SyncAttemptResult.Valid, rtt, offset, serverTime, t0);
    }

    private long GetEffectiveAcceptableRttMs()
    {
        return Math.Max(1L, acceptableRttMs);
    }

    private long GetEffectiveRequestTimeoutMs()
    {
        return Math.Max(1L, requestTimeoutMs);
    }

    private bool TryGetSyncUrl(out string url)
    {
        url = null;
        if (ipListManager == null)
            return false;

        string selectedIp = ipListManager.GetSelectedIP();
        if (string.IsNullOrEmpty(selectedIp))
            return false;

        url = $"http://{selectedIp}:4050/timeSync";
        return true;
    }

    private string BuildLastSuccessText()
    {
        if (lastSuccessfulRttMs < 0)
            return "No successful sync yet";

        return $"Last OK RTT:{lastSuccessfulRttMs}ms, Offset:{lastSuccessfulOffsetMs}ms";
    }

    private string BuildRoundStatsText(long bestObservedRtt, int timeoutCount, int overThresholdCount)
    {
        string bestRttText = bestObservedRtt == long.MaxValue
            ? "Best RTT:n/a"
            : $"Best RTT:{bestObservedRtt}ms";

        return $"{bestRttText}, timeout:{timeoutCount}, over:{overThresholdCount}";
    }

    private void EnsureSyncLoopRunning(string reason)
    {
        if (!autoStartSyncLoop || !isActiveAndEnabled)
            return;

        double now = Time.realtimeSinceStartupAsDouble;
        bool hasCoroutineHandle = syncLoopCoroutine != null;
        bool watchdogExpired =
            lastLoopHeartbeatRealtime > 0.0 &&
            now - lastLoopHeartbeatRealtime > Math.Max(0.5f, loopWatchdogTimeout);

        if (hasCoroutineHandle && isSyncLoopRunning && !watchdogExpired)
            return;

        RestartSyncLoop(reason);
    }

    private void RestartSyncLoop(string reason)
    {
        if (syncLoopCoroutine != null)
            StopCoroutine(syncLoopCoroutine);

        syncLoopCoroutine = StartCoroutine(SyncLoop());
        isSyncLoopRunning = true;
        lastLoopHeartbeatRealtime = Time.realtimeSinceStartupAsDouble;
        Debug.Log($"[TimeSync] Sync loop restarted: {reason}");
    }

    /// <summary>
    /// Get synchronized current timestamp (milliseconds)
    /// </summary>
    public static long GetSyncedTimestampMs()
    {
        if (!hasSynced)
            return GetLocalTimestampMs();

        return GetLocalTimestampMs() + timeOffsetMs;
    }

    public static long AddTimeOffset(long input)
    {
        if (!hasSynced)
            return input;
        return input + timeOffsetMs;
    }

    private static long GetLocalTimestampMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Parse server timestamp string (seconds)
    /// </summary>
    private bool TryParseServerTime(string timeStr, out long timeMs)
    {
        timeMs = 0;

        if (string.IsNullOrEmpty(timeStr))
            return false;

        if (!double.TryParse(
                timeStr,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double timeSec))
        {
            return false;
        }

        timeMs = (long)(timeSec * 1000);
        return true;
    }

    [Serializable]
    private class ServerTimeResponse
    {
        public string t2;
        public string t3;
    }

    private void OnDisable()
    {
        if (syncLoopCoroutine != null)
            StopCoroutine(syncLoopCoroutine);

        syncLoopCoroutine = null;
        isSyncInProgress = false;
        isSyncLoopRunning = false;
        lastLoopHeartbeatRealtime = -1.0;
        lastWatchdogCheckRealtime = -1.0;
    }
}
