// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core.Camera;

namespace Linkast.Core.Tests;

public class RecordingSessionTests
{
    private static StreamSession Session(Destination destination) => new(
        new DisplaySource(1), [], EncoderSettings.Default1080p60("libx264"), destination, NetworkSelection.SystemDefault, ReconnectPolicy.Default);

    private static readonly Destination ValidDestination = new("Test", new Uri("rtmp://localhost/live"), new Secret("key"));
    private static readonly Destination NoDestination = new("None", new Uri("rtmp://invalid/"), new Secret(""));
    private static readonly CanvasLayout EmptyCanvasLayout = new(
        new Dictionary<string, NormalizedRect>(),
        new Dictionary<string, NormalizedRect>(),
        []);

    [Fact]
    public void Recording_only_does_not_need_a_destination()
    {
        var s = Session(NoDestination) with { Broadcast = false, Recording = new RecordingSpec(RecordingFiles.DefaultFolder) };
        Assert.Empty(SessionValidator.Validate(s));
    }

    [Fact]
    public void Streaming_still_needs_a_destination()
    {
        var s = Session(NoDestination) with { Recording = new RecordingSpec(RecordingFiles.DefaultFolder) };
        Assert.NotEmpty(SessionValidator.Validate(s));
    }

    [Fact]
    public void Neither_streaming_nor_recording_is_rejected()
    {
        var s = Session(NoDestination) with { Broadcast = false };
        Assert.Contains(SessionValidator.Validate(s), e => e.Code == "recording.none");
    }

    [Fact]
    public void A_relative_folder_is_rejected()
    {
        var s = Session(NoDestination) with { Broadcast = false, Recording = new RecordingSpec("videos") };
        Assert.Contains(SessionValidator.Validate(s), e => e.Code == "recording.folder");
    }

    [Fact]
    public void Custom_recording_encoder_with_matching_fps_and_aspect_passes()
    {
        var s = Session(ValidDestination) with
        {
            Encoder = EncoderSettings.Default720p60("libx264"),
            Recording = new RecordingSpec(RecordingFiles.DefaultFolder)
            {
                Encoder = new EncoderSettings("libx264", 1920, 1080, 60, 12000),
            },
        };
        Assert.Empty(SessionValidator.Validate(s));
    }

    [Fact]
    public void Custom_recording_encoder_with_different_fps_is_rejected()
    {
        var s = Session(ValidDestination) with
        {
            Recording = new RecordingSpec(RecordingFiles.DefaultFolder)
            {
                Encoder = new EncoderSettings("libx264", 1280, 720, 30, 8000),
            },
        };
        Assert.Contains(SessionValidator.Validate(s), e => e.Code == "recording.fps");
    }

    [Fact]
    public void Custom_recording_encoder_with_different_aspect_is_rejected()
    {
        var s = Session(ValidDestination) with
        {
            Recording = new RecordingSpec(RecordingFiles.DefaultFolder)
            {
                Encoder = new EncoderSettings("libx264", 1080, 1920, 60, 10000),
            },
        };
        Assert.Contains(SessionValidator.Validate(s), e => e.Code == "recording.aspect");
    }

    [Fact]
    public void Vertical_recording_with_matching_fps_and_portrait_shape_passes()
    {
        var s = Session(ValidDestination) with
        {
            VerticalRecording = new VerticalRecordingSpec(
                RecordingFiles.DefaultFolder,
                new EncoderSettings("libx264", 1080, 1920, 60, 10000),
                EmptyCanvasLayout),
        };
        Assert.Empty(SessionValidator.Validate(s));
    }

    [Fact]
    public void Vertical_recording_that_is_landscape_or_square_is_rejected()
    {
        var s = Session(ValidDestination) with
        {
            VerticalRecording = new VerticalRecordingSpec(
                RecordingFiles.DefaultFolder,
                new EncoderSettings("libx264", 1920, 1080, 60, 10000),
                EmptyCanvasLayout),
        };
        Assert.Contains(SessionValidator.Validate(s), e => e.Code == "vertical.shape");
    }

    [Fact]
    public void Vertical_recording_with_different_fps_is_rejected()
    {
        var s = Session(ValidDestination) with
        {
            VerticalRecording = new VerticalRecordingSpec(
                RecordingFiles.DefaultFolder,
                new EncoderSettings("libx264", 1080, 1920, 30, 10000),
                EmptyCanvasLayout),
        };
        Assert.Contains(SessionValidator.Validate(s), e => e.Code == "vertical.fps");
    }

    [Fact]
    public void Vertical_recording_with_relative_folder_is_rejected()
    {
        var s = Session(ValidDestination) with
        {
            VerticalRecording = new VerticalRecordingSpec(
                "relative-dir",
                new EncoderSettings("libx264", 1080, 1920, 60, 10000),
                EmptyCanvasLayout),
        };
        Assert.Contains(SessionValidator.Validate(s), e => e.Code == "vertical.folder");
    }

    [Fact]
    public void NewPath_uses_optional_suffix_and_avoids_collisions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkast-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var when = new DateTime(2026, 11, 28, 21, 4, 9);
            var main = RecordingFiles.NewPath(dir, when);
            Assert.Equal(Path.Combine(dir, "Linkast 2026-11-28 21-04-09.mkv"), main);

            var vert1 = RecordingFiles.NewPath(dir, when, "vertical");
            Assert.Equal(Path.Combine(dir, "Linkast 2026-11-28 21-04-09 vertical.mkv"), vert1);

            File.WriteAllBytes(vert1, []);
            var vert2 = RecordingFiles.NewPath(dir, when, "vertical");
            Assert.Equal(Path.Combine(dir, "Linkast 2026-11-28 21-04-09 vertical (2).mkv"), vert2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
