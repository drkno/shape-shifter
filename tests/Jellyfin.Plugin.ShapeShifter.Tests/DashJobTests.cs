using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using Jellyfin.Plugin.ShapeShifter.Playback.Dash;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ShapeShifter.Tests;

/// <summary>Runs the DASH pipeline against a real ffmpeg (skipped when ffmpeg isn't on PATH).</summary>
public sealed class DashJobTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shapeshifter-dash-test-" + Guid.NewGuid().ToString("N"));

    public DashJobTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string? Which(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Append("/opt/homebrew/bin").Append("/usr/bin")
            .Select(d => Path.Combine(d, tool)).FirstOrDefault(File.Exists);

    private static string Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{tool} failed: {stderr.Result}");
        return stdout.Result;
    }

    /// <summary>30s 640x360 test pattern with two audio tracks (stereo + 5.1), as mkv.</summary>
    private string MakeSource(string name)
    {
        var path = Path.Combine(_dir, name);
        Run(Which("ffmpeg")!, "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=30",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=30",
            "-f", "lavfi", "-i", "sine=frequency=880:duration=30",
            "-map", "0", "-map", "1", "-map", "2",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "250",
            "-c:a", "aac", "-ac:a:1", "6", path);
        return path;
    }

    /// <summary>Decode time (seconds) of a media segment, as a DASH player places it: its tfdt over the init's timescale.</summary>
    private static double SegmentStart(string header, string segment) =>
        (double)Fmp4Timestamps.ReadDecodeTime(File.ReadAllBytes(segment))!.Value / Fmp4Timestamps.ReadTimescale(File.ReadAllBytes(header))!.Value;

    [Fact]
    public async Task ProducesAlignedSegmentsAndRestartsOnSeek()
    {
        if (Which("ffmpeg") is not { } ffmpeg || Which("ffprobe") is null)
        {
            return;
        }

        var options = new DashOptions
        {
            InputPath = MakeSource("source.mkv"),
            DurationSeconds = 30,
            VideoKbps = 1000,
            MaxWidth = 426,
            MaxHeight = 240,
            AudioStreamIndex = 2,
            AudioChannels = 2,
        };
        Assert.Equal(8, options.SegmentCount);

        await using var job = new DashJob(options, ffmpeg, Path.Combine(_dir, "job"), NullLogger.Instance);
        await job.StartAsync();

        var videoHeader = await job.GetHeaderAsync(DashManifest.VideoRepresentation, default);
        var audioHeader = await job.GetHeaderAsync(DashManifest.AudioRepresentation, default);
        Assert.NotNull(videoHeader);
        Assert.NotNull(audioHeader);
        var headerCopy = Path.Combine(_dir, "init-0.m4s");
        File.Copy(videoHeader, headerCopy);
        var audioHeaderCopy = Path.Combine(_dir, "init-1.m4s");
        File.Copy(audioHeader, audioHeaderCopy);
        audioHeader = audioHeaderCopy;

        for (var n = 0; n < 3; n++)
        {
            var segment = await job.GetSegmentAsync(DashManifest.VideoRepresentation, n, default);
            Assert.NotNull(segment);
            Assert.Equal(n * 4.0, SegmentStart(headerCopy, segment), 1);
            Assert.NotNull(await job.GetSegmentAsync(DashManifest.AudioRepresentation, n, default));
        }

        // Jump ahead past what the first run has produced: forces a restart at segment 6.
        // The header fetched from the first run must still place the new run's segments correctly.
        var far = await job.GetSegmentAsync(DashManifest.VideoRepresentation, 6, default);
        Assert.NotNull(far);
        Assert.Equal(24.0, SegmentStart(headerCopy, far), 1);
        Assert.Equal(File.ReadAllBytes(headerCopy), File.ReadAllBytes((await job.GetHeaderAsync(DashManifest.VideoRepresentation, default))!));
        var farAudio = await job.GetSegmentAsync(DashManifest.AudioRepresentation, 6, default);
        Assert.Equal(24.0, SegmentStart(audioHeader!, farAudio!), 1);

        // The final (shorter) segment arrives once ffmpeg finishes.
        Assert.NotNull(await job.GetSegmentAsync(DashManifest.VideoRepresentation, 7, default));
        Assert.Null(await job.GetSegmentAsync(DashManifest.VideoRepresentation, 8, default));
    }

    [Fact]
    public void FilterPathEscapingSurvivesFfmpegParsing()
    {
        if (Which("ffmpeg") is not { } ffmpeg)
        {
            return;
        }

        var dir = Path.Combine(_dir, "Bob's Movie: Part 1 [2020]");
        Directory.CreateDirectory(dir);
        var source = MakeSource("plain.mkv");
        var awkward = Path.Combine(dir, "Bob's: cut.mkv");
        File.Copy(source, awkward);

        // movie= takes a path through the same two parsing levels as subtitles=.
        var graph = $"movie=filename='{DashCommandBuilder.EscapeFilterPath(awkward)}'[m];[0:v][m]overlay[v]";
        Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-t", "1", "-i", source, "-filter_complex", graph, "-map", "[v]", "-f", "null", "-");
    }

    [Fact]
    public async Task BurnsExternalSubtitleFromAwkwardPath()
    {
        // Jellyfin's ffmpeg has libass; many distro/homebrew builds don't.
        if (Which("ffmpeg") is not { } ffmpeg || !Run(ffmpeg, "-hide_banner", "-filters").Contains(" subtitles ", StringComparison.Ordinal))
        {
            return;
        }

        var subDir = Path.Combine(_dir, "Bob's Movie: Part 1 [2020]");
        Directory.CreateDirectory(subDir);
        var srt = Path.Combine(subDir, "Bob's Movie.en.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,500 --> 00:00:09,000\nHello there\n\n");

        var options = new DashOptions
        {
            InputPath = MakeSource("subs.mkv"),
            DurationSeconds = 30,
            VideoKbps = 800,
            AudioStreamIndex = 1,
            Subtitle = new BurnSubtitle(srt, 0, false),
        };
        await using var job = new DashJob(options, ffmpeg, Path.Combine(_dir, "subjob"), NullLogger.Instance);
        await job.StartAsync();
        Assert.NotNull(await job.GetSegmentAsync(DashManifest.VideoRepresentation, 0, default));
    }

    [Fact]
    public void ManifestIsStaticAndTemplated()
    {
        var options = new DashOptions { InputPath = "/x.mkv", DurationSeconds = 600, OffsetSeconds = 120, VideoKbps = 4000, AudioStreamIndex = 1, AudioChannels = 2 };
        var mpd = XDocument.Parse(DashManifest.Build(options, "/video/:/transcode/universal/session/s/secret/", 1280, 720, "eng"));
        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        Assert.Equal("static", (string?)mpd.Root!.Attribute("type"));
        Assert.Equal("PT480S", (string?)mpd.Root.Attribute("mediaPresentationDuration"));
        var templates = mpd.Descendants(ns + "SegmentTemplate").ToList();
        Assert.Equal(2, templates.Count);
        Assert.Equal("/video/:/transcode/universal/session/s/secret/0/$Number$.m4s", (string?)templates[0].Attribute("media"));
        Assert.Equal("/video/:/transcode/universal/session/s/secret/1/header", (string?)templates[1].Attribute("initialization"));
        Assert.Equal("eng", (string?)mpd.Descendants(ns + "AdaptationSet").Last().Attribute("lang"));
    }
}
