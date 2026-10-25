// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linkast.Core;
using Linkast.Engine.FFmpeg;
using Microsoft.Extensions.Logging;

namespace Linkast.App.ViewModels;

/// <summary>Quality › Find the best settings: encoder speed test, upload test, one suggestion to apply.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The upload test sends this much, above every platform's 1080p60 limit, to see whether the line keeps up.</summary>
    private const int UploadTestKbps = 9000;

    private static readonly TimeSpan UploadTestLength = TimeSpan.FromSeconds(10);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindBestSettingsCommand))]
    public partial bool IsTestingQuality { get; set; }

    /// <summary>What the test is doing, or what it found.</summary>
    [ObservableProperty]
    public partial string? AutoQualityStatus { get; set; }

    /// <summary>Typed by the user when the upload can't be measured (any platform but Twitch). In Mbps.</summary>
    [ObservableProperty]
    public partial string UploadSpeedMbps { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecommendation))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecommendationCommand))]
    public partial QualityRecommendation? Recommendation { get; set; }

    public bool HasRecommendation => Recommendation is not null;

    public string RecommendationText => Recommendation is not { } r
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture,
            $"{r.Width} × {r.Height} at {r.Fps} FPS, {r.VideoBitrateKbps} kbps, {Encoders.FirstOrDefault(e => e.Id == r.EncoderId)?.DisplayName ?? r.EncoderId}");

    partial void OnRecommendationChanged(QualityRecommendation? value) => OnPropertyChanged(nameof(RecommendationText));

    /// <summary>Twitch has a test mode that never goes live; other platforms would show the test to viewers.</summary>
    public bool CanMeasureUpload => SelectedPlatform.Platform == StreamPlatform.Twitch && !StreamKey.IsEmpty;

    private bool CanFindBestSettings() => !IsTestingQuality && !IsActive && _probeResults.Any(r => r.Works);

    [RelayCommand(CanExecute = nameof(CanFindBestSettings))]
    private async Task FindBestSettingsAsync()
    {
        if (_engine is null)
        {
            return;
        }

        Recommendation = null;
        int? typedKbps = double.TryParse(UploadSpeedMbps.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps) && mbps > 0
            ? (int)(mbps * 1000)
            : null;
        if (!CanMeasureUpload && typedKbps is null)
        {
            AutoQualityStatus = "Enter your upload speed first (in Mbps). Twitch streamers can skip this: Linkast measures it.";
            return;
        }

        IsTestingQuality = true;
        try
        {
            var speeds = new List<EncoderSpeed>();
            var working = _probeResults.Where(r => r.Works).Select(r => r.Encoder).ToList();
            for (var i = 0; i < working.Count; i++)
            {
                AutoQualityStatus = $"Testing encoders on this PC ({i + 1} of {working.Count}: {working[i].DisplayName})…";
                if (await EncoderBenchmark.RunAsync(_engine.Installation, working[i]) is { } speed)
                {
                    speeds.Add(speed);
                    AppendLog($"Encoder speed {speed.EncoderId}: {speed.FramesPerSecond:0} fps at 1080p");
                }
            }

            var uploadKbps = typedKbps;
            if (CanMeasureUpload)
            {
                AutoQualityStatus = "Measuring your upload to Twitch (test mode: nothing goes live)…";
                uploadKbps = await MeasureTwitchUploadAsync(speeds) ?? typedKbps;
                if (uploadKbps is null)
                {
                    return; // the status already says why
                }
            }

            Recommendation = QualityAdvisor.Recommend(SelectedPlatform.Platform, uploadKbps!.Value, speeds);
            AutoQualityStatus = Recommendation?.Reason ?? "No encoder finished the test, so there is nothing to suggest.";
            AppendLog($"Suggested quality: {RecommendationText}. {Recommendation?.Reason}");
        }
        finally
        {
            IsTestingQuality = false;
        }
    }

    /// <summary>
    /// Streams a test picture to Twitch with <c>?bandwidthtest=true</c> (Twitch receives it but never shows it)
    /// and measures what actually left this PC on the chosen network. Returns kbps, or null on failure.
    /// </summary>
    private async Task<int?> MeasureTwitchUploadAsync(IReadOnlyList<EncoderSpeed> speeds)
    {
        if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var server))
        {
            AutoQualityStatus = "The Twitch server address isn't valid.";
            return null;
        }

        var encoder = speeds.Where(s => s.IsHardware).MaxBy(s => s.FramesPerSecond)?.EncoderId ?? SelectedEncoder?.Id ?? "libx264";
        var testKey = new Secret(StreamKey.Reveal() + "?bandwidthtest=true");
        Redactor.Shared.Register(testKey);
        var session = new StreamSession(
            new TestPatternSource(),
            [new TestToneSource()],
            new EncoderSettings(encoder, 1920, 1080, 60, UploadTestKbps),
            new Destination("Twitch bandwidth test", server, testKey),
            SelectedNetwork.ToSelection(),
            ReconnectPolicy.Disabled);

        await using var test = new FfmpegStreamEngine(_engine!.Installation, _transport, logger: _loggerFactory.CreateLogger<FfmpegStreamEngine>());
        var samples = new List<double>();
        StreamError? failure = null;
        test.TelemetryUpdated += (_, t) =>
        {
            // Skip the first seconds: the connection ramps up.
            if (t.State == StreamState.Live && t.ConnectionUptime > TimeSpan.FromSeconds(3) && t.Network is { OutgoingKbps: > 0 } n)
            {
                lock (samples)
                {
                    samples.Add(n.OutgoingKbps);
                }
            }
        };
        test.StateChanged += (_, e) => failure = e.Error ?? failure;

        try
        {
            await test.StartAsync(session);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < UploadTestLength + TimeSpan.FromSeconds(10) && StreamStateMachine.IsActive(test.State))
            {
                lock (samples)
                {
                    if (samples.Count >= UploadTestLength.TotalSeconds - 3)
                    {
                        break;
                    }
                }

                await Task.Delay(250);
            }

            await test.StopAsync();
        }
        catch (StreamFailureException ex)
        {
            failure = ex.Error;
        }

        double[] measured;
        lock (samples)
        {
            measured = [.. samples];
        }

        if (measured.Length < 3)
        {
            AutoQualityStatus = failure is not null
                ? $"The upload test didn't work: {failure.UserMessage} Enter your upload speed instead."
                : "The upload test didn't get going. Check the stream key, or enter your upload speed instead.";
            return null;
        }

        // The test sends at most UploadTestKbps; reaching it means the line can do at least that.
        var average = measured.Average();
        AppendLog($"Twitch upload test: {average:0} kbps sustained (test stream at {UploadTestKbps} kbps)");
        return average >= UploadTestKbps * 0.92 ? 20_000 : (int)average;
    }

    private bool CanApplyRecommendation() => Recommendation is not null && !IsActive;

    [RelayCommand(CanExecute = nameof(CanApplyRecommendation))]
    private void ApplyRecommendation()
    {
        if (Recommendation is not { } r)
        {
            return;
        }

        SelectedResolution = Resolutions.FirstOrDefault(o => o.Width == r.Width && o.Height == r.Height) ?? SelectedResolution;
        SelectedFrameRate = r.Fps;
        VideoBitrateKbps = r.VideoBitrateKbps;
        SelectedEncoder = Encoders.FirstOrDefault(e => e.Id == r.EncoderId) ?? SelectedEncoder;
        AutoQualityStatus = "Applied. You can still change any of it below.";
        AppendLog($"Applied suggested quality: {RecommendationText}");
        Recommendation = null;
    }

    [RelayCommand]
    private static void OpenSpeedTest() =>
        Process.Start(new ProcessStartInfo("https://speed.cloudflare.com/") { UseShellExecute = true });
}
