using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.ShapeShifter.Playback.Dash;

/// <summary>Subtitle to burn into the video. RelativeIndex is the index among the file's subtitle streams.</summary>
public sealed record BurnSubtitle(string? ExternalPath, int RelativeIndex, bool IsImage);

/// <summary>Everything needed to start (or restart) a DASH transcode. Independent of Jellyfin types for testing.</summary>
public sealed record DashOptions
{
    public const int SegmentSeconds = 4;

    public required string InputPath { get; init; }

    /// <summary>Gets where the presentation starts in the source, in seconds (Plex's offset=).</summary>
    public double OffsetSeconds { get; init; }

    /// <summary>Gets the source duration in seconds.</summary>
    public required double DurationSeconds { get; init; }

    public int VideoKbps { get; init; } = 8000;

    public int? MaxWidth { get; init; }

    public int? MaxHeight { get; init; }

    /// <summary>Gets the absolute stream index of the audio track, or null for none.</summary>
    public int? AudioStreamIndex { get; init; }

    public int AudioChannels { get; init; } = 2;

    public BurnSubtitle? Subtitle { get; init; }

    /// <summary>Gets the ffmpeg H.264 encoder (libx264, h264_nvenc, h264_qsv, h264_vaapi, h264_videotoolbox, h264_amf).</summary>
    public string Encoder { get; init; } = "libx264";

    public string? VaapiDevice { get; init; }

    public double PresentationSeconds => Math.Max(0, DurationSeconds - OffsetSeconds);

    public int SegmentCount => (int)Math.Ceiling(PresentationSeconds / SegmentSeconds);

    public int AudioKbps => AudioChannels > 2 ? 384 : 160;
}

/// <summary>Builds the ffmpeg command line for one DASH transcode run.</summary>
public static class DashCommandBuilder
{
    /// <summary>
    /// Arguments for a run that starts at <paramref name="startSegment"/> of the presentation and writes
    /// into <paramref name="outputDir"/>. Keyframes are forced on segment boundaries and timestamps
    /// are moved to the segment's place in the presentation by <see cref="DashJob"/> when served,
    /// so every run lines up with the one static MPD whichever segment it starts at.
    /// </summary>
    public static List<string> Build(DashOptions o, int startSegment, string outputDir)
    {
        var start = startSegment * DashOptions.SegmentSeconds;
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin", "-y" };
        var vaapi = o.Encoder == "h264_vaapi";
        if (vaapi && !string.IsNullOrEmpty(o.VaapiDevice))
        {
            args.AddRange(["-vaapi_device", o.VaapiDevice]);
        }

        var seek = o.OffsetSeconds + start;
        if (seek > 0)
        {
            args.AddRange(["-ss", Seconds(seek)]);
        }

        args.AddRange(["-i", o.InputPath]);

        args.AddRange(["-filter_complex", VideoFilter(o) + "[v]", "-map", "[v]"]);
        if (o.AudioStreamIndex is { } audio)
        {
            args.AddRange(["-map", "0:" + audio.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);

        var kbps = o.VideoKbps.ToString(CultureInfo.InvariantCulture);
        args.AddRange(["-c:v", o.Encoder]);
        args.AddRange(o.Encoder switch
        {
            "libx264" => ["-preset", "veryfast", "-sc_threshold", "0", "-x264-params", "open-gop=0"],
            "h264_nvenc" => ["-preset", "p4", "-forced-idr", "1"],
            "h264_qsv" => ["-preset", "veryfast", "-forced_idr", "1"],
            "h264_videotoolbox" => ["-realtime", "1"],
            _ => [],
        });
        args.AddRange(["-b:v", kbps + "k", "-maxrate", kbps + "k", "-bufsize", (o.VideoKbps * 2).ToString(CultureInfo.InvariantCulture) + "k"]);
        args.AddRange(["-profile:v", "high"]);
        if (!vaapi)
        {
            args.AddRange(["-level", "4.1"]);
        }

        args.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{DashOptions.SegmentSeconds})"]);

        if (o.AudioStreamIndex is not null)
        {
            args.AddRange(["-c:a", "aac", "-ac", o.AudioChannels.ToString(CultureInfo.InvariantCulture), "-ar", "48000", "-b:a", o.AudioKbps.ToString(CultureInfo.InvariantCulture) + "k"]);
        }

        args.AddRange([
            "-f", "dash",

            // No edit list: every run's init segment is then identical, and DashJob puts each
            // run's segments in place by rewriting their decode times (see Fmp4Timestamps).
            "-format_options", "use_editlist=0",
            "-seg_duration", DashOptions.SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-use_template", "1",
            "-use_timeline", "0",
            "-window_size", "0",
            "-remove_at_exit", "0",
            "-init_seg_name", "init-$RepresentationID$.m4s",
            "-media_seg_name", "chunk-$RepresentationID$-$Number%05d$.m4s",
            "-adaptation_sets", o.AudioStreamIndex is null ? "id=0,streams=v" : "id=0,streams=v id=1,streams=a",
            Path.Combine(outputDir, "manifest.mpd"),
        ]);
        return args;
    }

    private static string VideoFilter(DashOptions o)
    {
        var chain = new StringBuilder("[0:v:0]");
        if (o.Subtitle is { } sub)
        {
            if (sub.IsImage && sub.ExternalPath is null)
            {
                chain.Clear().Append(CultureInfo.InvariantCulture, $"[0:v:0][0:s:{sub.RelativeIndex}]overlay,");
            }
            else
            {
                chain.Append(CultureInfo.InvariantCulture, $"subtitles=filename='{EscapeFilterPath(sub.ExternalPath ?? o.InputPath)}'");
                if (sub.ExternalPath is null)
                {
                    chain.Append(CultureInfo.InvariantCulture, $":si={sub.RelativeIndex}");
                }

                chain.Append(',');
            }
        }

        var w = o.MaxWidth?.ToString(CultureInfo.InvariantCulture) ?? "iw";
        var h = o.MaxHeight?.ToString(CultureInfo.InvariantCulture) ?? "ih";
        if (o.Encoder == "h264_vaapi")
        {
            chain.Append(CultureInfo.InvariantCulture, $"format=nv12,hwupload,scale_vaapi=w='min({w},iw)':h='min({h},ih)':force_original_aspect_ratio=decrease");
        }
        else
        {
            chain.Append(CultureInfo.InvariantCulture, $"scale=w='min({w},iw)':h='min({h},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p");
        }

        return chain.ToString();
    }

    /// <summary>
    /// Escapes a path for a single-quoted filtergraph option value. Two parsers see it: the graph
    /// parser (quotes) and the filter option parser (':' and '\\'). Verified with ffmpeg's movie filter.
    /// </summary>
    internal static string EscapeFilterPath(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "'\\\\\\''", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);

    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
}
