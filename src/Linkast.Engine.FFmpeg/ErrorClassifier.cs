// SPDX-License-Identifier: GPL-3.0-or-later
using Linkast.Core;

namespace Linkast.Engine.FFmpeg;

/// <summary>
/// Maps FFmpeg stderr lines to classified errors. Rules are ordered: the first match wins.
/// Lines must already be redacted; the matched line is kept as <see cref="StreamError.Detail"/>.
/// </summary>
public static class ErrorClassifier
{
    private sealed record Rule(ErrorCategory Category, string Code, string Message, string[] Needles);

    private static readonly Rule[] Rules =
    [
        // Encoder
        new(ErrorCategory.Encoder, "encoder.nvenc.unavailable", "The NVIDIA encoder is unavailable. Update the GPU driver or choose another encoder.",
            ["No NVENC capable devices found", "Cannot load nvcuda.dll", "Cannot load nvEncodeAPI", "Driver does not support the required nvenc API version"]),
        new(ErrorCategory.Encoder, "encoder.nvenc.session", "The NVIDIA encoder could not start a session. Close other apps using NVENC or choose another encoder.",
            ["OpenEncodeSessionEx failed", "InitializeEncoder failed", "incompatible client key"]),
        new(ErrorCategory.Encoder, "encoder.amf.unavailable", "The AMD encoder is unavailable on this system.",
            ["amfrt64.dll failed to open", "Failed to create  hardware device context (AMF)", "Failed to create hardware device context (AMF)"]),
        new(ErrorCategory.Encoder, "encoder.qsv.unavailable", "The Intel Quick Sync encoder is unavailable on this system.",
            ["Error creating a MFX session", "mfx implementation is not supported"]),
        new(ErrorCategory.Encoder, "encoder.open", "The encoder rejected these settings. Try a lower resolution, frame rate or another encoder.",
            ["Error while opening encoder", "Could not open encoder", "Error initializing output stream", "Unknown encoder"]),

        // Capture
        new(ErrorCategory.Capture, "capture.dda", "Desktop Duplication capture failed. Try Windows Graphics Capture or another monitor.",
            ["ddagrab", "DuplicateOutput", "DXGI_ERROR"]),
        new(ErrorCategory.Capture, "capture.wgc", "Windows Graphics Capture failed. The window may have closed or capture is blocked.",
            ["gfxcapture", "GraphicsCapture", "CreateForWindow", "CreateForMonitor"]),
        new(ErrorCategory.Capture, "capture.texture", "The capture source could not be read from the GPU.",
            ["Could not create the texture", "hwdownload", "Failed to transfer data"]),

        // Audio
        new(ErrorCategory.Audio, "audio.pipe", "An audio source stopped delivering data.",
            [@"\\.\pipe\linkast-", "pipe\\linkast-"]),

        // Network (as seen from FFmpeg; the relay usually reports a more precise reason)
        new(ErrorCategory.Network, "network.refused", "The server refused the connection. Check the server URL.",
            ["Connection refused", "Error number -10061"]),
        new(ErrorCategory.Network, "network.timeout", "The connection to the server timed out.",
            ["Connection timed out", "Error number -10060", "Operation timed out"]),
        new(ErrorCategory.Network, "network.dns", "The server name could not be resolved.",
            ["Failed to resolve hostname", "getaddrinfo", "No such host"]),
        new(ErrorCategory.Network, "network.rejected", "The server rejected the stream. Check the stream key.",
            ["Server error", "NetStream.Publish.BadName", "NetConnection.Connect.Rejected", "Authentication failed", "Unauthorized"]),
        new(ErrorCategory.Network, "network.reset", "The connection to the server was lost.",
            ["Connection reset", "Error number -10054", "Error number -10053", "Broken pipe", "End of file", "Error in the push function",
             "av_interleaved_write_frame", "Error writing trailer", "Error muxing a packet", "I/O error", "Cannot open connection",
             "Error opening output", "Input/output error"]),

        // Configuration
        new(ErrorCategory.Configuration, "config.ffmpeg", "FFmpeg rejected an option. This is a Linkast bug; please report it.",
            ["Option not found", "Unrecognized option", "Error parsing", "Invalid argument", "No such filter", "Error applying option"]),
    ];

    /// <summary>
    /// Classifies a single line. Only lines tagged <c>[error]</c> or <c>[fatal]</c> (or untagged
    /// lines printed by FFmpeg's top level) are considered.
    /// </summary>
    public static StreamError? Classify(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!IsErrorLine(line))
        {
            return null;
        }

        foreach (var rule in Rules)
        {
            foreach (var needle in rule.Needles)
            {
                if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return new StreamError(rule.Category, rule.Code, rule.Message, line.Trim());
                }
            }
        }

        return null;
    }

    /// <summary>Picks the most specific error from the recent lines, preferring the earliest root cause.</summary>
    public static StreamError ClassifyExit(int exitCode, IReadOnlyList<string> recentLines)
    {
        ArgumentNullException.ThrowIfNull(recentLines);
        StreamError? fallback = null;
        foreach (var line in recentLines)
        {
            var e = Classify(line);
            if (e is null)
            {
                continue;
            }

            // Network "reset" lines are often a consequence of an earlier specific error; keep looking.
            if (e.Code != "network.reset" && e.Code != "config.ffmpeg")
            {
                return e;
            }

            fallback ??= e;
        }

        if (fallback is not null)
        {
            return fallback;
        }

        var tail = recentLines.Count > 0 ? recentLines[^1] : null;
        return new StreamError(ErrorCategory.Process, "process.exit", $"FFmpeg stopped unexpectedly (exit code {exitCode}).", tail);
    }

    internal static bool IsErrorLine(string line) =>
        line.Contains("[error]", StringComparison.Ordinal)
        || line.Contains("[fatal]", StringComparison.Ordinal)
        || (!line.Contains("[warning]", StringComparison.Ordinal)
            && !line.Contains("[info]", StringComparison.Ordinal)
            && !line.Contains("[verbose]", StringComparison.Ordinal)
            && !line.Contains("[debug]", StringComparison.Ordinal));
}
