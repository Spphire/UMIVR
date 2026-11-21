using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Minimal depth probe for Quest:
/// 1) Reads global _EnvironmentDepthTexture.
/// 2) Pulls data to CPU via AsyncGPUReadback.
/// 3) Benchmarks whether 30Hz is sustainable without blocking the main recorder.
/// </summary>
public class DepthCpuProbe : MonoBehaviour
{
    [Header("Depth Source")]
    [SerializeField] private string depthTextureProperty = "_EnvironmentDepthTexture";

    [Header("Schedule")]
    [SerializeField] private bool startOnEnable = false;
    [SerializeField, Min(1)] private int targetHz = 30;
    [SerializeField, Min(1)] private int benchmarkSeconds = 20;
    [SerializeField] private bool autoStopAfterBenchmark = true;

    [Header("CPU Copy")]
    [SerializeField] private bool keepLatestCpuBytes = true;
    [SerializeField] private bool copyOnReadback = true;

    [Header("GPU Downsample (ComputeShader)")]
    [SerializeField] private bool useComputeDownsample = true;
    [SerializeField] private ComputeShader depthDownsampleCompute;
    [SerializeField, Min(1)] private int downsampleFactor = 2;
    [SerializeField] private RenderTextureFormat preferredComputeInputFormat = RenderTextureFormat.RFloat;
    [SerializeField] private RenderTextureFormat preferredComputeOutputFormat = RenderTextureFormat.RFloat;
    [SerializeField] private bool skipStagingAfterCompute = true;

    [Header("Readback Compatibility")]
    [SerializeField] private bool useStagingRenderTexture = true;
    [SerializeField] private RenderTextureFormat preferredStagingFormat = RenderTextureFormat.RFloat;
    [SerializeField] private FilterMode stagingFilterMode = FilterMode.Point;

    [Header("Readback Queue")]
    [SerializeField, Min(1)] private int maxInFlight = 2;

    [Header("Benchmark Timing")]
    [SerializeField, Min(0.01f)] private float maxCountedDeltaSeconds = 0.1f;

    [Header("Debug")]
    [SerializeField] private bool logEverySecond = true;
    [SerializeField] private bool logTextureInfoOnce = true;

    private bool _running;
    private bool _loggedTextureInfo;
    private bool _asyncReadbackSupportedLogged;
    private bool _computeSupportLogged;
    private bool _computeKernelLogged;
    private bool _computePathLogged;
    private bool _computeUnavailableLogged;

    private double _startTime;
    private double _activeElapsedTime;
    private double _lastUpdateWallTime;
    private double _nextTickTime;
    private double _lastPeriodicLogTime;

    private long _scheduledCount;
    private long _completedCount;
    private long _errorCount;
    private long _skipInFlightCount;
    private long _missingTextureCount;
    private long _computeUsedCount;
    private long _computeFallbackCount;
    private int _inFlightCount;
    private int _peakInFlightCount;
    private int _frameCount;
    private int _lastFrameCountAtPeriodic;
    private long _lastScheduledAtPeriodic;
    private long _lastCompletedAtPeriodic;

    private double _latencyMsSum;
    private double _latencyMsMax;

    private int _latestWidth;
    private int _latestHeight;
    private RenderTextureFormat _latestFormat;
    private long _latestTimestampMs;
    private byte[] _latestDepthBytes;
    private RenderTexture _stagingRt;
    private RenderTextureFormat _effectiveStagingFormat;
    private RenderTexture _computeInputRt;
    private RenderTexture _computeOutputRt;
    private RenderTextureFormat _effectiveComputeInputFormat;
    private RenderTextureFormat _effectiveComputeOutputFormat;
    private int _computeKernel = -1;

    private static readonly int InputTexId = Shader.PropertyToID("_InputTex");
    private static readonly int OutputTexId = Shader.PropertyToID("_OutputTex");
    private static readonly int InputWidthId = Shader.PropertyToID("_InputWidth");
    private static readonly int InputHeightId = Shader.PropertyToID("_InputHeight");
    private static readonly int OutputWidthId = Shader.PropertyToID("_OutputWidth");
    private static readonly int OutputHeightId = Shader.PropertyToID("_OutputHeight");
    private static readonly int FactorId = Shader.PropertyToID("_Factor");

    public bool IsRunning => _running;
    public long ScheduledCount => _scheduledCount;
    public long CompletedCount => _completedCount;
    public long ErrorCount => _errorCount;
    public long SkipInFlightCount => _skipInFlightCount;
    public long MissingTextureCount => _missingTextureCount;
    public double AvgLatencyMs => _completedCount > 0 ? _latencyMsSum / _completedCount : 0.0;
    public double MaxLatencyMs => _latencyMsMax;

    [ContextMenu("Start 30Hz Benchmark")]
    public void Start30HzBenchmark()
    {
        targetHz = 30;
        StartProbe();
    }

    [ContextMenu("Start Probe")]
    public void StartProbe()
    {
        ResetStats();

        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            Debug.LogError("[DepthCpuProbe] AsyncGPUReadback is not supported on this device/runtime.");
            return;
        }

        if (!_computeSupportLogged)
        {
            _computeSupportLogged = true;
            Debug.Log($"[DepthCpuProbe] supportsComputeShaders={SystemInfo.supportsComputeShaders}");
        }

        _running = true;
        _loggedTextureInfo = false;
        _computePathLogged = false;
        _computeUnavailableLogged = false;
        _inFlightCount = 0;
        _peakInFlightCount = 0;
        _frameCount = 0;
        _lastFrameCountAtPeriodic = 0;
        _lastScheduledAtPeriodic = 0;
        _lastCompletedAtPeriodic = 0;

        _startTime = Time.unscaledTimeAsDouble;
        _activeElapsedTime = 0.0;
        _lastUpdateWallTime = _startTime;
        _nextTickTime = _startTime;
        _lastPeriodicLogTime = _startTime;

        Debug.Log(
            $"[DepthCpuProbe] Start: targetHz={targetHz}, benchmarkSeconds={benchmarkSeconds}, " +
            $"useCompute={useComputeDownsample}, factor={downsampleFactor}, useStaging={useStagingRenderTexture}, " +
            $"skipStagingAfterCompute={skipStagingAfterCompute}, maxInFlight={Mathf.Max(1, maxInFlight)}"
        );
    }

    [ContextMenu("Stop Probe")]
    public void StopProbe()
    {
        if (!_running)
            return;

        _running = false;
        _inFlightCount = 0;

        LogSummary("stop");
    }

    public bool TryGetLatestDepthFrame(out DepthCpuFrame frame)
    {
        if (_latestDepthBytes == null || _latestDepthBytes.Length == 0)
        {
            frame = default;
            return false;
        }

        frame = new DepthCpuFrame
        {
            width = _latestWidth,
            height = _latestHeight,
            format = _latestFormat,
            timestampMs = _latestTimestampMs,
            data = _latestDepthBytes
        };

        return true;
    }

    private void OnEnable()
    {
        if (startOnEnable)
            StartProbe();
    }

    private void OnDisable()
    {
        StopProbe();
    }

    private void OnDestroy()
    {
        ReleaseComputeTextures();
        ReleaseStagingTexture();
    }

    private void Update()
    {
        if (!_running)
            return;

        double now = Time.unscaledTimeAsDouble;
        double wallDelta = Math.Max(0.0, now - _lastUpdateWallTime);
        _lastUpdateWallTime = now;

        // Ignore large focus/background gaps in "active" benchmark timing.
        double countedDelta = Math.Min(wallDelta, Math.Max(0.01f, maxCountedDeltaSeconds));

        _frameCount++;
        _activeElapsedTime += countedDelta;

        if (autoStopAfterBenchmark && _activeElapsedTime >= benchmarkSeconds)
        {
            StopProbe();
            return;
        }

        if (logEverySecond && now - _lastPeriodicLogTime >= 1.0)
        {
            double logWindowSec = Math.Max(0.001, now - _lastPeriodicLogTime);
            int frameDelta = _frameCount - _lastFrameCountAtPeriodic;
            long scheduledDelta = _scheduledCount - _lastScheduledAtPeriodic;
            long completedDelta = _completedCount - _lastCompletedAtPeriodic;

            _lastFrameCountAtPeriodic = _frameCount;
            _lastScheduledAtPeriodic = _scheduledCount;
            _lastCompletedAtPeriodic = _completedCount;
            _lastPeriodicLogTime = now;
            LogPeriodic(logWindowSec, frameDelta, scheduledDelta, completedDelta);
        }

        if (now < _nextTickTime)
            return;

        double tickPeriod = 1.0 / Math.Max(1, targetHz);
        _nextTickTime += tickPeriod;
        if (_nextTickTime <= now)
        {
            double missedTicks = Math.Floor((now - _nextTickTime) / tickPeriod) + 1.0;
            _nextTickTime += missedTicks * tickPeriod;
        }

        int allowedInFlight = Mathf.Max(1, maxInFlight);
        if (_inFlightCount >= allowedInFlight)
        {
            _skipInFlightCount++;
            return;
        }

        var depthTexture = Shader.GetGlobalTexture(depthTextureProperty) as RenderTexture;
        if (depthTexture == null)
        {
            _missingTextureCount++;
            return;
        }

        if (logTextureInfoOnce && !_loggedTextureInfo)
        {
            _loggedTextureInfo = true;
            Debug.Log(
                $"[DepthCpuProbe] Source texture: {depthTexture.width}x{depthTexture.height}, " +
                $"format={depthTexture.format}"
            );
        }

        if (!_asyncReadbackSupportedLogged)
        {
            _asyncReadbackSupportedLogged = true;
            Debug.Log($"[DepthCpuProbe] supportsAsyncGPUReadback={SystemInfo.supportsAsyncGPUReadback}");
        }

        RenderTexture readbackTexture = depthTexture;
        bool fromComputePath = false;
        if (useComputeDownsample && downsampleFactor > 1 && depthDownsampleCompute != null && SystemInfo.supportsComputeShaders)
        {
            bool computeReady = EnsureComputeTextures(depthTexture.width, depthTexture.height);
            if (computeReady)
            {
                Graphics.Blit(depthTexture, _computeInputRt);
                computeReady = RunComputeDownsample(depthTexture.width, depthTexture.height);
            }

            if (computeReady)
            {
                _computeUsedCount++;
                readbackTexture = _computeOutputRt;
                _latestFormat = _computeOutputRt.format;
                fromComputePath = true;

                if (!_computePathLogged)
                {
                    _computePathLogged = true;
                    Debug.Log(
                        $"[DepthCpuProbe] Compute downsample active: " +
                        $"{depthTexture.width}x{depthTexture.height} -> {readbackTexture.width}x{readbackTexture.height}, " +
                        $"factor={downsampleFactor}, output={_latestFormat}"
                    );
                }
            }
            else if (!_computeUnavailableLogged)
            {
                _computeFallbackCount++;
                _computeUnavailableLogged = true;
                Debug.LogWarning("[DepthCpuProbe] Compute downsample unavailable this run, fallback to direct readback.");
            }
            else
            {
                _computeFallbackCount++;
            }
        }

        if (useStagingRenderTexture && !(fromComputePath && skipStagingAfterCompute))
        {
            if (!EnsureStagingTexture(readbackTexture.width, readbackTexture.height))
            {
                _errorCount++;
                return;
            }

            Graphics.Blit(readbackTexture, _stagingRt);
            readbackTexture = _stagingRt;
            _latestFormat = _stagingRt.format;
        }
        else if (!fromComputePath)
        {
            _latestFormat = depthTexture.format;
        }

        _scheduledCount++;
        _inFlightCount++;
        if (_inFlightCount > _peakInFlightCount)
            _peakInFlightCount = _inFlightCount;
        double requestStartTime = Time.realtimeSinceStartupAsDouble;

        _latestWidth = readbackTexture.width;
        _latestHeight = readbackTexture.height;

        AsyncGPUReadback.Request(readbackTexture, 0, request => OnReadbackComplete(request, requestStartTime));
    }

    private void OnReadbackComplete(AsyncGPUReadbackRequest request, double requestStartTime)
    {
        _inFlightCount = Math.Max(0, _inFlightCount - 1);

        if (!_running)
            return;

        if (request.hasError)
        {
            _errorCount++;
            return;
        }

        _completedCount++;

        double latencyMs = (Time.realtimeSinceStartupAsDouble - requestStartTime) * 1000.0;
        _latencyMsSum += latencyMs;
        if (latencyMs > _latencyMsMax)
            _latencyMsMax = latencyMs;

        if (keepLatestCpuBytes)
        {
            var raw = request.GetData<byte>();
            if (_latestDepthBytes == null || _latestDepthBytes.Length != raw.Length)
                _latestDepthBytes = new byte[raw.Length];

            if (copyOnReadback)
                raw.CopyTo(_latestDepthBytes);
        }

        _latestTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private void ResetStats()
    {
        _scheduledCount = 0;
        _completedCount = 0;
        _errorCount = 0;
        _skipInFlightCount = 0;
        _missingTextureCount = 0;
        _computeUsedCount = 0;
        _computeFallbackCount = 0;
        _inFlightCount = 0;
        _peakInFlightCount = 0;
        _activeElapsedTime = 0.0;
        _lastUpdateWallTime = 0.0;
        _frameCount = 0;
        _lastFrameCountAtPeriodic = 0;
        _lastScheduledAtPeriodic = 0;
        _lastCompletedAtPeriodic = 0;

        _latencyMsSum = 0.0;
        _latencyMsMax = 0.0;
    }

    private void LogPeriodic(double logWindowSec, int frameDelta, long scheduledDelta, long completedDelta)
    {
        double approxFps = frameDelta / logWindowSec;
        double scheduledPerSec = scheduledDelta / logWindowSec;
        double completedPerSec = completedDelta / logWindowSec;

        Debug.Log(
            $"[DepthCpuProbe][1s] targetHz={targetHz} scheduled={_scheduledCount} completed={_completedCount} " +
            $"err={_errorCount} skipInFlight={_skipInFlightCount} missTex={_missingTextureCount} " +
            $"computeUsed={_computeUsedCount} computeFallback={_computeFallbackCount} " +
            $"inFlight={_inFlightCount}/{Mathf.Max(1, maxInFlight)} peakInFlight={_peakInFlightCount} " +
            $"approxFps={approxFps:F2} scheduledPerSec={scheduledPerSec:F2} completedPerSec={completedPerSec:F2} " +
            $"avgLatency={AvgLatencyMs:F2}ms maxLatency={MaxLatencyMs:F2}ms"
        );
    }

    private void LogSummary(string reason)
    {
        double wallElapsed = Math.Max(0.001, Time.unscaledTimeAsDouble - _startTime);
        double activeElapsed = Math.Max(0.001, _activeElapsedTime);
        double wallCompletedHz = _completedCount / wallElapsed;
        double wallScheduledHz = _scheduledCount / wallElapsed;
        double activeCompletedHz = _completedCount / activeElapsed;
        double activeScheduledHz = _scheduledCount / activeElapsed;
        long bytes = _latestDepthBytes?.Length ?? 0;

        Debug.Log(
            $"[DepthCpuProbe][summary:{reason}] wallElapsed={wallElapsed:F2}s activeElapsed={activeElapsed:F2}s targetHz={targetHz} " +
            $"scheduled={_scheduledCount} completed={_completedCount} err={_errorCount} " +
            $"skipInFlight={_skipInFlightCount} missTex={_missingTextureCount} " +
            $"computeUsed={_computeUsedCount} computeFallback={_computeFallbackCount} " +
            $"peakInFlight={_peakInFlightCount}/{Mathf.Max(1, maxInFlight)} " +
            $"maxCountedDelta={Math.Max(0.01f, maxCountedDeltaSeconds):F3}s " +
            $"scheduledHz(active/wall)={activeScheduledHz:F2}/{wallScheduledHz:F2} " +
            $"completedHz(active/wall)={activeCompletedHz:F2}/{wallCompletedHz:F2} " +
            $"avgLatency={AvgLatencyMs:F2}ms maxLatency={MaxLatencyMs:F2}ms " +
            $"lastFrame={_latestWidth}x{_latestHeight} {_latestFormat} bytes={bytes}"
        );
    }

    private bool EnsureStagingTexture(int width, int height)
    {
        var requiredFormat = GetAvailableStagingFormat();
        if (_stagingRt != null &&
            _stagingRt.width == width &&
            _stagingRt.height == height &&
            _effectiveStagingFormat == requiredFormat)
        {
            return true;
        }

        ReleaseStagingTexture();

        _effectiveStagingFormat = requiredFormat;
        _stagingRt = new RenderTexture(width, height, 0, _effectiveStagingFormat)
        {
            filterMode = stagingFilterMode,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false
        };
        _stagingRt.Create();

        if (!_stagingRt.IsCreated())
        {
            Debug.LogError($"[DepthCpuProbe] Failed to create staging RenderTexture: {width}x{height}, {_effectiveStagingFormat}");
            ReleaseStagingTexture();
            return false;
        }

        Debug.Log($"[DepthCpuProbe] Staging RT: {width}x{height}, format={_effectiveStagingFormat}");
        return true;
    }

    private bool EnsureComputeTextures(int inputWidth, int inputHeight)
    {
        int factor = Math.Max(1, downsampleFactor);
        int outputWidth = Math.Max(1, inputWidth / factor);
        int outputHeight = Math.Max(1, inputHeight / factor);

        var inputFmt = GetAvailableComputeInputFormat();
        var outputFmt = GetAvailableComputeOutputFormat();
        if (outputFmt == RenderTextureFormat.Default)
            return false;

        bool inputOk = _computeInputRt != null &&
                       _computeInputRt.width == inputWidth &&
                       _computeInputRt.height == inputHeight &&
                       _effectiveComputeInputFormat == inputFmt;

        bool outputOk = _computeOutputRt != null &&
                        _computeOutputRt.width == outputWidth &&
                        _computeOutputRt.height == outputHeight &&
                        _effectiveComputeOutputFormat == outputFmt;

        if (inputOk && outputOk)
            return true;

        ReleaseComputeTextures();

        _effectiveComputeInputFormat = inputFmt;
        _effectiveComputeOutputFormat = outputFmt;

        _computeInputRt = CreateRt(inputWidth, inputHeight, _effectiveComputeInputFormat, false);
        _computeOutputRt = CreateRt(outputWidth, outputHeight, _effectiveComputeOutputFormat, true);
        if (_computeInputRt == null || _computeOutputRt == null)
        {
            ReleaseComputeTextures();
            return false;
        }

        Debug.Log(
            $"[DepthCpuProbe] Compute RTs: in={inputWidth}x{inputHeight} {_effectiveComputeInputFormat}, " +
            $"out={outputWidth}x{outputHeight} {_effectiveComputeOutputFormat}"
        );
        return true;
    }

    private bool RunComputeDownsample(int inputWidth, int inputHeight)
    {
        if (_computeKernel < 0)
        {
            try
            {
                _computeKernel = depthDownsampleCompute.FindKernel("KMain");
            }
            catch (Exception e)
            {
                Debug.LogError($"[DepthCpuProbe] FindKernel(KMain) failed: {e.Message}");
                return false;
            }
        }

        if (!_computeKernelLogged)
        {
            _computeKernelLogged = true;
            Debug.Log($"[DepthCpuProbe] Compute kernel = {_computeKernel}");
        }

        int outW = _computeOutputRt.width;
        int outH = _computeOutputRt.height;

        depthDownsampleCompute.SetTexture(_computeKernel, InputTexId, _computeInputRt);
        depthDownsampleCompute.SetTexture(_computeKernel, OutputTexId, _computeOutputRt);
        depthDownsampleCompute.SetInt(InputWidthId, inputWidth);
        depthDownsampleCompute.SetInt(InputHeightId, inputHeight);
        depthDownsampleCompute.SetInt(OutputWidthId, outW);
        depthDownsampleCompute.SetInt(OutputHeightId, outH);
        depthDownsampleCompute.SetInt(FactorId, Math.Max(1, downsampleFactor));

        int gx = Mathf.CeilToInt(outW / 8.0f);
        int gy = Mathf.CeilToInt(outH / 8.0f);
        depthDownsampleCompute.Dispatch(_computeKernel, gx, gy, 1);
        return true;
    }

    private RenderTextureFormat GetAvailableComputeInputFormat()
    {
        if (SystemInfo.SupportsRenderTextureFormat(preferredComputeInputFormat))
            return preferredComputeInputFormat;

        if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RHalf))
            return RenderTextureFormat.RHalf;

        if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            return RenderTextureFormat.ARGBHalf;

        return RenderTextureFormat.ARGB32;
    }

    private RenderTextureFormat GetAvailableComputeOutputFormat()
    {
        if (SupportsComputeOutputFormat(preferredComputeOutputFormat))
            return preferredComputeOutputFormat;

        if (SupportsComputeOutputFormat(RenderTextureFormat.RFloat))
            return RenderTextureFormat.RFloat;

        if (SupportsComputeOutputFormat(RenderTextureFormat.RHalf))
            return RenderTextureFormat.RHalf;

        return RenderTextureFormat.Default;
    }

    private bool SupportsComputeOutputFormat(RenderTextureFormat fmt)
    {
        return SystemInfo.SupportsRenderTextureFormat(fmt) &&
               SystemInfo.SupportsRandomWriteOnRenderTextureFormat(fmt);
    }

    private RenderTexture CreateRt(int width, int height, RenderTextureFormat fmt, bool randomWrite)
    {
        var rt = new RenderTexture(width, height, 0, fmt)
        {
            enableRandomWrite = randomWrite,
            filterMode = stagingFilterMode,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false
        };
        rt.Create();
        if (!rt.IsCreated())
        {
            Debug.LogError($"[DepthCpuProbe] Failed to create RT: {width}x{height}, format={fmt}, randomWrite={randomWrite}");
            rt.Release();
            Destroy(rt);
            return null;
        }

        return rt;
    }

    private RenderTextureFormat GetAvailableStagingFormat()
    {
        if (SystemInfo.SupportsRenderTextureFormat(preferredStagingFormat))
            return preferredStagingFormat;

        if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RHalf))
            return RenderTextureFormat.RHalf;

        if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            return RenderTextureFormat.ARGBHalf;

        return RenderTextureFormat.ARGB32;
    }

    private void ReleaseStagingTexture()
    {
        if (_stagingRt == null)
            return;

        if (_stagingRt.IsCreated())
            _stagingRt.Release();
        Destroy(_stagingRt);
        _stagingRt = null;
    }

    private void ReleaseComputeTextures()
    {
        if (_computeInputRt != null)
        {
            if (_computeInputRt.IsCreated())
                _computeInputRt.Release();
            Destroy(_computeInputRt);
            _computeInputRt = null;
        }

        if (_computeOutputRt != null)
        {
            if (_computeOutputRt.IsCreated())
                _computeOutputRt.Release();
            Destroy(_computeOutputRt);
            _computeOutputRt = null;
        }
    }
}

[Serializable]
public struct DepthCpuFrame
{
    public int width;
    public int height;
    public RenderTextureFormat format;
    public long timestampMs;
    public byte[] data;
}
