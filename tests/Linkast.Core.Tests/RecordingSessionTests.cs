// SPDX-License-Identifier: GPL-3.0-or-later
namespace Linkast.Core.Tests;

public class RecordingSessionTests
{
    private static StreamSession Session(Destination destination) => new(
        new DisplaySource(1), [], EncoderSettings.Default1080p60("libx264"), destination, NetworkSelection.SystemDefault, ReconnectPolicy.Default);

    private static readonly Destination NoDestination = new("None", new Uri("rtmp://invalid/"), new Secret(""));

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
}
