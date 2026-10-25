// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Linkast.Core.Camera;
using Microsoft.ML.OnnxRuntime;

namespace Linkast.Effects;

/// <summary>
/// Person segmentation with RobustVideoMatting (MobileNetV3) on ONNX Runtime. The model is
/// recurrent: its hidden state carries information between frames, which keeps edges stable and
/// avoids flicker. GPU inference uses Windows' own DirectML; if that fails, the CPU is used and the
/// reason is reported. Not thread-safe: one instance per worker thread.
/// </summary>
public sealed class RvmSegmenter : IDisposable
{
    private static readonly string[] InputNames = ["src", "r1i", "r2i", "r3i", "r4i", "downsample_ratio"];
    private static readonly string[] OutputNames = ["fgr", "pha", "r1o", "r2o", "r3o", "r4o"];

    private readonly InferenceSession _session;
    private readonly float[] _input;
    private readonly OrtValue _inputValue;
    private readonly OrtValue _ratioValue;
    private readonly OrtValue _zeroState;
    private IDisposableReadOnlyCollection<OrtValue>? _previous;

    private RvmSegmenter(InferenceSession session, string provider, string? gpuFailure, int width, int height, float downsampleRatio)
    {
        _session = session;
        Provider = provider;
        GpuFailure = gpuFailure;
        Width = width;
        Height = height;
        _input = new float[3 * width * height];
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, 3, height, width]);
        _ratioValue = OrtValue.CreateTensorValueFromMemory(new[] { downsampleRatio }, [1]);
        _zeroState = OrtValue.CreateTensorValueFromMemory(new float[1], [1, 1, 1, 1]);
    }

    /// <summary>"GPU (DirectML)" or "CPU".</summary>
    public string Provider { get; }

    /// <summary>Why the GPU could not be used, when <see cref="Provider"/> is CPU but the GPU was requested.</summary>
    public string? GpuFailure { get; }

    /// <summary>Model input and mask width.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Input size and internal downsampling for each quality level (multiples of 32, 16:9).</summary>
    public static (int Width, int Height, float Ratio) Shape(SegmentationQuality quality) => quality switch
    {
        SegmentationQuality.Performance => (384, 224, 0.5f),
        SegmentationQuality.Quality => (768, 448, 0.4f),
        _ => (512, 288, 0.5f),
    };

    public static RvmSegmenter Create(SegmentationQuality quality, bool preferGpu)
    {
        var path = ModelCatalog.Locate(ModelCatalog.Rvm);
        var (w, h, ratio) = Shape(quality);
        string? gpuFailure = null;

        if (preferGpu)
        {
            var gpuOptions = new SessionOptions
            {
                // Required by the DirectML provider.
                EnableMemoryPattern = false,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            try
            {
                gpuOptions.AppendExecutionProvider_DML(0);
                var session = new InferenceSession(path, gpuOptions);
                var segmenter = new RvmSegmenter(session, "GPU (DirectML)", null, w, h, ratio);
                segmenter.Warmup();
                return segmenter;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                gpuFailure = ex.Message;
            }
            finally
            {
                gpuOptions.Dispose();
            }
        }

        var cpuOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,

            // Leave most cores to the game and the encoder.
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4),
        };
        try
        {
            var cpu = new RvmSegmenter(new InferenceSession(path, cpuOptions), "CPU", gpuFailure, w, h, ratio);
            cpu.Warmup();
            return cpu;
        }
        finally
        {
            cpuOptions.Dispose();
        }
    }

    /// <summary>
    /// Segments <paramref name="frame"/> and writes person alpha (0-255) into <paramref name="mask"/>,
    /// which must be <see cref="Width"/> × <see cref="Height"/>. Returns the inference time.
    /// </summary>
    public TimeSpan Process(BgraFrame frame, SegmentationMask mask)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(mask);
        if (mask.Width != Width || mask.Height != Height)
        {
            throw new ArgumentException("Mask size does not match the model input.", nameof(mask));
        }

        Preprocess(frame);
        var sw = Stopwatch.StartNew();
        var states = _previous is null
            ? new[] { _zeroState, _zeroState, _zeroState, _zeroState }
            : new[] { _previous[2], _previous[3], _previous[4], _previous[5] };

        using var runOptions = new RunOptions();
        var outputs = _session.Run(runOptions, InputNames, [_inputValue, states[0], states[1], states[2], states[3], _ratioValue], OutputNames);
        var elapsed = sw.Elapsed;

        // pha: [1,1,H,W] floats 0..1
        var pha = outputs[1].GetTensorDataAsSpan<float>();
        var alpha = mask.Alpha;
        for (var i = 0; i < alpha.Length; i++)
        {
            var v = pha[i];
            alpha[i] = v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)((v * 255) + 0.5f);
        }

        // Keep this frame's recurrent state for the next frame; release the one before.
        _previous?.Dispose();
        _previous = outputs;
        return elapsed;
    }

    /// <summary>Forgets the temporal state, for example after a camera switch or a long pause.</summary>
    public void Reset()
    {
        _previous?.Dispose();
        _previous = null;
    }

    public void Dispose()
    {
        _previous?.Dispose();
        _inputValue.Dispose();
        _ratioValue.Dispose();
        _zeroState.Dispose();
        _session.Dispose();
    }

    /// <summary>Resizes BGRA to the model's RGB planar float input (0..1), bilinear.</summary>
    private void Preprocess(BgraFrame frame)
    {
        int w = Width, h = Height, plane = w * h;
        var src = frame.Pixels;
        var sx = (float)frame.Width / w;
        var sy = (float)frame.Height / h;
        const float inv = 1f / 255f;
        for (var y = 0; y < h; y++)
        {
            var fy = Math.Max(0, ((y + 0.5f) * sy) - 0.5f);
            var y0 = Math.Min((int)fy, frame.Height - 1);
            var y1 = Math.Min(y0 + 1, frame.Height - 1);
            var ty = fy - y0;
            for (var x = 0; x < w; x++)
            {
                var fx = Math.Max(0, ((x + 0.5f) * sx) - 0.5f);
                var x0 = Math.Min((int)fx, frame.Width - 1);
                var x1 = Math.Min(x0 + 1, frame.Width - 1);
                var tx = fx - x0;
                int a = ((y0 * frame.Width) + x0) * 4, b = ((y0 * frame.Width) + x1) * 4, c = ((y1 * frame.Width) + x0) * 4, d = ((y1 * frame.Width) + x1) * 4;
                var o = (y * w) + x;
                for (var ch = 0; ch < 3; ch++)
                {
                    // BGRA -> RGB planes: channel 0 = R (offset 2), 1 = G (1), 2 = B (0).
                    var off = 2 - ch;
                    var top = src[a + off] + ((src[b + off] - src[a + off]) * tx);
                    var bottom = src[c + off] + ((src[d + off] - src[c + off]) * tx);
                    _input[(ch * plane) + o] = (top + ((bottom - top) * ty)) * inv;
                }
            }
        }
    }

    private void Warmup()
    {
        // First run compiles kernels (seconds on DirectML); do it now, not on the first live frame.
        var frame = new BgraFrame(64, 36);
        var mask = new SegmentationMask(Width, Height);
        Process(frame, mask);
        Reset();
    }
}
