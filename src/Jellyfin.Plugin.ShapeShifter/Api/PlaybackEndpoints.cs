using System.Globalization;
using System.Text;
using Jellyfin.Plugin.ShapeShifter.Data;
using Jellyfin.Plugin.ShapeShifter.Playback;
using Jellyfin.Plugin.ShapeShifter.Playback.Dash;
using Jellyfin.Plugin.ShapeShifter.Plex;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>Direct play (file parts), subtitles and the universal transcoder.</summary>
public sealed class PlaybackEndpoints : IPlexEndpoints
{
    private const string TranscodeBase = "/video/:/transcode/universal";
    private const string MusicBase = "/music/:/transcode/universal";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IdMap _ids;
    private readonly MetadataMapper _mapper;
    private readonly JellyfinLoopback _loopback;
    private readonly TranscodeSessions _sessions;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PlaybackEndpoints> _logger;

    public PlaybackEndpoints(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IUserDataManager userDataManager,
        IdMap ids,
        MetadataMapper mapper,
        JellyfinLoopback loopback,
        TranscodeSessions sessions,
        IMediaEncoder mediaEncoder,
        IConfigurationManager configurationManager,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _userDataManager = userDataManager;
        _ids = ids;
        _mapper = mapper;
        _loopback = loopback;
        _sessions = sessions;
        _mediaEncoder = mediaEncoder;
        _configurationManager = configurationManager;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PlaybackEndpoints>();
    }

    public void Map(PlexRouter.IRouteBuilder routes)
    {
        routes.Get("/library/parts/{part}/{*rest}", Part)
            .Put("/library/parts/{part}", SelectStreams)
            .Get("/library/streams/{stream}", Subtitle)
            .Get(TranscodeBase + "/decision", Decision)
            .Get(TranscodeBase + "/start.m3u8", StartHls)
            .Get(TranscodeBase + "/start", StartHls)
            .Get(TranscodeBase + "/start.mpd", StartDash)
            .Get(TranscodeBase + "/session/{session}/{secret}/{*rest}", SessionProxy, anonymous: true)
            .Get(TranscodeBase + "/stop", Stop)
            .Get(TranscodeBase + "/ping", Ping)
            .Get("/:/transcode/universal/ping", Ping)
            .Get(MusicBase + "/decision", ctx => Decision(ctx, music: true))
            .Get(MusicBase + "/start.mp3", StartMusic)
            .Get(MusicBase + "/start", StartMusic)
            .Get(MusicBase + "/stop", Stop)
            .Get(MusicBase + "/ping", Ping);
    }

    private (BaseItem Item, MediaSourceInfo Source)? ResolvePart(PlexContext ctx, string partKey)
    {
        if (!long.TryParse(partKey, out var partId) || _ids.Resolve(IdMap.Part, partId) is not { } mapped)
        {
            return null;
        }

        var pieces = mapped.Split('|', 2);
        if (!Guid.TryParse(pieces[0], out var itemId) || _libraryManager.GetItemById(itemId) is not { } item || !item.IsVisible(ctx.User))
        {
            return null;
        }

        var source = _mediaSourceManager.GetStaticMediaSources(item, false, ctx.User).FirstOrDefault(s => s.Id == pieces[1]);
        return source is null ? null : (item, source);
    }

    private async Task Part(PlexContext ctx)
    {
        // /indexes/sd|hd are PMS's BIF seek thumbnails; Jellyfin has none in that format.
        if (ctx.Route("rest").StartsWith("indexes", StringComparison.Ordinal))
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var resolved = ResolvePart(ctx, ctx.Route("part"));
        if (resolved is null || !File.Exists(resolved.Value.Source.Path))
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var path = resolved.Value.Source.Path;
        if (!ContentTypes.TryGetContentType(path, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        var download = ctx.QueryBool("download") ? Path.GetFileName(path) : null;
        await Results.File(path, contentType, download, enableRangeProcessing: true).ExecuteAsync(ctx.Http).ConfigureAwait(false);
    }

    private async Task SelectStreams(PlexContext ctx)
    {
        var resolved = ResolvePart(ctx, ctx.Route("part"));
        if (resolved is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var (item, _) = resolved.Value;
        var data = _userDataManager.GetUserData(ctx.User, item);
        if (data is not null)
        {
            if (StreamIndex(ctx.Query("audioStreamID")) is { } audio)
            {
                data.AudioStreamIndex = audio;
            }

            if (ctx.Query("subtitleStreamID") is { } sub)
            {
                data.SubtitleStreamIndex = sub == "0" ? -1 : StreamIndex(sub);
            }

            _userDataManager.SaveUserData(ctx.User, item, data, UserDataSaveReason.UpdateUserData, ctx.Http.RequestAborted);
        }

        await ctx.WriteEmpty().ConfigureAwait(false);
    }

    private int? StreamIndex(string? plexStreamId)
    {
        if (!long.TryParse(plexStreamId, out var id) || _ids.Resolve(IdMap.Stream, id) is not { } mapped)
        {
            return null;
        }

        return int.TryParse(mapped.Split('|')[^1], out var index) ? index : null;
    }

    private async Task Subtitle(PlexContext ctx)
    {
        // Clients may ask for /library/streams/{id}.srt; the extension picks the format.
        var streamKey = ctx.Route("stream");
        var extension = Path.GetExtension(streamKey).TrimStart('.');
        if (extension.Length > 0)
        {
            streamKey = Path.GetFileNameWithoutExtension(streamKey);
        }

        if (!long.TryParse(streamKey, out var id) || _ids.Resolve(IdMap.Stream, id) is not { } mapped)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var pieces = mapped.Split('|');
        if (!Guid.TryParse(pieces[0], out var itemId) || _libraryManager.GetItemById(itemId) is not { } item || !item.IsVisible(ctx.User))
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var stream = _mediaSourceManager.GetStaticMediaSources(item, false, ctx.User)
            .FirstOrDefault(s => s.Id == pieces[1])?.MediaStreams
            .FirstOrDefault(s => s.Index.ToString(CultureInfo.InvariantCulture) == pieces[2]);
        var codec = stream?.Codec?.ToLowerInvariant();
        var format = ctx.Query("format") ?? (extension is "srt" or "vtt" or "ass" or "ssa" ? extension : null) ?? (codec is "ass" or "ssa" or "vtt" or "srt" ? codec : "srt");
        await _loopback.ProxyAsync(ctx, $"/Videos/{itemId:N}/{pieces[1]}/Subtitles/{pieces[2]}/0/Stream.{format}").ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the item targeted by a transcoder request: path=/library/metadata/{rk}, plus mediaIndex.
    /// </summary>
    private (BaseItem Item, MediaSourceInfo Source)? ResolveTranscodeTarget(PlexContext ctx)
    {
        var rk = ctx.Query("path")?.Split('?')[0].Split('/').LastOrDefault();
        var id = _ids.ResolveGuid(IdMap.Item, rk);
        if (id is null || _libraryManager.GetItemById(id.Value) is not { } item || !item.IsVisible(ctx.User))
        {
            return null;
        }

        var sources = _mediaSourceManager.GetStaticMediaSources(item, false, ctx.User);
        var index = Math.Clamp(ctx.QueryInt("mediaIndex") ?? 0, 0, Math.Max(0, sources.Count - 1));
        return sources.Count == 0 ? null : (item, sources[index]);
    }

    private Task Decision(PlexContext ctx) => Decision(ctx, music: false);

    private async Task Decision(PlexContext ctx, bool music)
    {
        var target = ResolveTranscodeTarget(ctx);
        if (target is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var (item, source) = target.Value;
        var maxKbps = ctx.QueryInt("maxVideoBitrate");
        var bitrateOk = maxKbps is null || source.Bitrate is null || source.Bitrate <= maxKbps * 1000L;
        var subtitleBurn = ctx.Query("subtitles") == "burn";
        var directPlay = ctx.Query("directPlay") != "0" && bitrateOk && !subtitleBurn;
        if (music && !directPlay)
        {
            // Music transcodes are a single MP3 stream (see StartMusic).
            var musicMeta = _mapper.Map(item, ctx.User, new MapOptions { Detailed = true, IncludeMedia = false });
            var musicMedia = _mapper.MapMedia(item, source, true).Set("selected", true)
                .Set("protocol", "http").Set("container", "mp3").Set("audioCodec", "mp3");
            musicMedia.Children.First(c => c.Name == "Part").Set("decision", "transcode").Set("protocol", "http").Set("container", "mp3");
            musicMeta.Add(musicMedia);
            await ctx.Write(PlexNode.Container()
                .Set("allowSync", false)
                .Set("directPlayDecisionCode", 3000)
                .Set("generalDecisionCode", 1001)
                .Set("generalDecisionText", "Transcoding.")
                .Set("transcodeDecisionCode", 1001)
                .Set("transcodeDecisionText", "Transcoding.")
                .Set("identifier", MetadataMapper.LibraryIdentifier)
                .Add(musicMeta)
                .WithSize()).ConfigureAwait(false);
            return;
        }

        var meta = _mapper.Map(item, ctx.User, new MapOptions { Detailed = true, IncludeMedia = false });
        var data = _userDataManager.GetUserData(ctx.User, item);
        var subtitleChoice = ctx.Query("subtitles") == "none" || ctx.Query("subtitleStreamID") == "0" ? -1 : StreamIndex(ctx.Query("subtitleStreamID")) ?? data?.SubtitleStreamIndex;
        var media = _mapper.MapMedia(item, source, true, StreamIndex(ctx.Query("audioStreamID")) ?? data?.AudioStreamIndex, subtitleChoice).Set("selected", true);
        var part = media.Children.First(c => c.Name == "Part");
        if (directPlay)
        {
            part.Set("decision", "directplay");
        }
        else
        {
            var dash = ctx.Query("protocol") == "dash";
            var container = dash ? "mp4" : "mpegts";
            var protocol = dash ? "dash" : "hls";
            media.Set("protocol", protocol).Set("container", container).Set("videoCodec", "h264").Set("audioCodec", "aac");
            part.Set("decision", "transcode").Set("protocol", protocol).Set("container", container);
        }

        meta.Add(media);
        var node = PlexNode.Container()
            .Set("allowSync", false)
            .Set("directPlayDecisionCode", directPlay ? 1000 : 3000)
            .Set("directPlayDecisionText", directPlay ? "Direct play OK." : "App cannot direct play this item.")
            .Set("generalDecisionCode", 1000)
            .Set("generalDecisionText", directPlay ? "Direct play OK." : "Transcoding OK.")
            .Set("transcodeDecisionCode", directPlay ? null : 1001)
            .Set("transcodeDecisionText", directPlay ? null : "Transcoding.")
            .Set("identifier", MetadataMapper.LibraryIdentifier)
            .Set("librarySectionID", meta.Get("librarySectionID"))
            .Set("librarySectionTitle", meta.Get("librarySectionTitle"))
            .Set("librarySectionUUID", meta.Get("librarySectionUUID"))
            .Set("mediaTagPrefix", "/system/bundle/media/flags/")
            .Set("mediaTagVersion", 1)
            .Add(meta)
            .WithSize();
        await ctx.Write(node).ConfigureAwait(false);
    }

    private async Task StartHls(PlexContext ctx)
    {
        var target = ResolveTranscodeTarget(ctx);
        if (target is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var (item, source) = target.Value;
        var plexSession = ctx.Query("session") ?? ctx.Param("X-Plex-Session-Identifier") ?? Guid.NewGuid().ToString("N");
        if (await _sessions.RemoveAsync(plexSession).ConfigureAwait(false) is { } previous)
        {
            await StopJellyfinEncodingAsync(previous).ConfigureAwait(false);
        }

        var session = _sessions.Create(plexSession, item.Id, source.Id, "plex-" + ctx.ClientIdentifier);

        var q = new List<string>
        {
            "MediaSourceId=" + Uri.EscapeDataString(source.Id),
            "PlaySessionId=" + session.PlaySessionId,
            "DeviceId=" + Uri.EscapeDataString(session.DeviceId),
            "VideoCodec=h264",
            "AudioCodec=aac",
            "TranscodingMaxAudioChannels=" + (ctx.QueryInt("maxAudioChannels") ?? 2).ToString(CultureInfo.InvariantCulture),
            "SegmentContainer=ts",
            "MinSegments=1",
            "BreakOnNonKeyFrames=True",
            "EnableAutoStreamCopy=" + (ctx.Query("directStream") != "0" ? "true" : "false"),
        };

        if (ctx.QueryInt("maxVideoBitrate") is { } kbps)
        {
            q.Add("VideoBitrate=" + (kbps * 1000L).ToString(CultureInfo.InvariantCulture));
            q.Add("MaxStreamingBitrate=" + (kbps * 1000L).ToString(CultureInfo.InvariantCulture));
        }

        if (ctx.Query("videoResolution") is { } res && res.Split('x') is [var w, var h])
        {
            q.Add("MaxWidth=" + w);
            q.Add("MaxHeight=" + h);
        }

        if (ctx.QueryLong("offset") is { } offsetSeconds && offsetSeconds > 0)
        {
            q.Add("StartTimeTicks=" + (offsetSeconds * TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture));
        }

        var data = _userDataManager.GetUserData(ctx.User, item);
        if (StreamIndex(ctx.Query("audioStreamID")) is { } audio)
        {
            q.Add("AudioStreamIndex=" + audio.ToString(CultureInfo.InvariantCulture));
        }
        else if (data?.AudioStreamIndex is { } savedAudio)
        {
            q.Add("AudioStreamIndex=" + savedAudio.ToString(CultureInfo.InvariantCulture));
        }

        var subtitle = StreamIndex(ctx.Query("subtitleStreamID")) ?? data?.SubtitleStreamIndex;
        if (subtitle is >= 0 && ctx.Query("subtitles") != "none")
        {
            q.Add("SubtitleStreamIndex=" + subtitle.Value.ToString(CultureInfo.InvariantCulture));
            q.Add("SubtitleMethod=Encode");
        }

        var (status, contentType, body) = await _loopback.GetTextAsync($"/Videos/{item.Id:N}/master.m3u8?{string.Join('&', q)}", ctx.Http.RequestAborted).ConfigureAwait(false);
        if (status != 200)
        {
            _logger.LogWarning("Jellyfin refused transcode for {Item}: {Status} {Body}", item.Name, status, body);
            await ctx.Status(500).ConfigureAwait(false);
            return;
        }

        await WritePlaylist(ctx, session, body, contentType).ConfigureAwait(false);
    }

    private async Task SessionProxy(PlexContext ctx)
    {
        var session = _sessions.Get(ctx.Route("session"), ctx.Route("secret"));
        if (session is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var rest = ctx.Route("rest");
        if (session.Dash is { } dash)
        {
            await ServeDashFile(ctx, dash, rest).ConfigureAwait(false);
            return;
        }

        var upstreamPath = $"/Videos/{session.ItemId:N}/{rest}{ctx.Http.Request.QueryString}";
        if (rest.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var (status, contentType, body) = await _loopback.GetTextAsync(upstreamPath, ctx.Http.RequestAborted).ConfigureAwait(false);
            if (status != 200)
            {
                await ctx.Status(status).ConfigureAwait(false);
                return;
            }

            await WritePlaylist(ctx, session, body, contentType).ConfigureAwait(false);
            return;
        }

        await _loopback.ProxyAsync(ctx, upstreamPath).ConfigureAwait(false);
    }

    /// <summary>
    /// Rewrites playlist URIs (relative to /Videos/{id}/) onto our session path, and strips the
    /// Jellyfin api_key Jellyfin embeds in them so it never reaches Plex clients.
    /// </summary>
    private static Task WritePlaylist(PlexContext ctx, TranscodeSession session, string playlist, string? contentType)
    {
        var prefix = $"{TranscodeBase}/session/{Uri.EscapeDataString(session.PlexSessionId)}/{session.Secret}/";
        var sb = new StringBuilder();
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                line = prefix + StripApiKey(line);
            }
            else if (line.Contains("URI=\"", StringComparison.Ordinal))
            {
                var start = line.IndexOf("URI=\"", StringComparison.Ordinal) + 5;
                var end = line.IndexOf('"', start);
                line = line[..start] + prefix + StripApiKey(line[start..end]) + line[end..];
            }

            sb.Append(line).Append('\n');
        }

        ctx.Http.Response.ContentType = contentType ?? "application/vnd.apple.mpegurl";
        return ctx.Http.Response.WriteAsync(sb.ToString(), ctx.Http.RequestAborted);
    }

    private static string StripApiKey(string uri)
    {
        var q = uri.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return uri;
        }

        var kept = uri[(q + 1)..].Split('&').Where(p => !p.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("ApiKey=", StringComparison.OrdinalIgnoreCase));
        return uri[..q] + "?" + string.Join('&', kept);
    }

    /// <summary>Music transcode: Jellyfin's progressive MP3 stream, capped at ?musicBitrate= (kbps).</summary>
    private async Task StartMusic(PlexContext ctx)
    {
        var target = ResolveTranscodeTarget(ctx);
        if (target is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var (item, source) = target.Value;
        var kbps = Math.Clamp(ctx.QueryInt("musicBitrate") ?? ctx.QueryInt("maxAudioBitrate") ?? 320, 64, 320);
        var q = new List<string>
        {
            "static=false",
            "audioCodec=mp3",
            "container=mp3",
            "audioBitRate=" + (kbps * 1000).ToString(CultureInfo.InvariantCulture),
            "mediaSourceId=" + Uri.EscapeDataString(source.Id),
            "deviceId=" + Uri.EscapeDataString("plex-" + ctx.ClientIdentifier),
        };
        if (ctx.QueryLong("offset") is { } offset and > 0)
        {
            q.Add("startTimeTicks=" + (offset * TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture));
        }

        await _loopback.ProxyAsync(ctx, $"/Audio/{item.Id:N}/stream.mp3?{string.Join('&', q)}").ConfigureAwait(false);
    }

    private async Task Stop(PlexContext ctx)
    {
        if (ctx.Query("session") is { } plexSession && await _sessions.RemoveAsync(plexSession).ConfigureAwait(false) is { } session && session.Dash is null)
        {
            await StopJellyfinEncodingAsync(session).ConfigureAwait(false);
        }

        await ctx.WriteEmpty().ConfigureAwait(false);
    }

    private async Task StopJellyfinEncodingAsync(TranscodeSession session)
    {
        try
        {
            using var response = await _loopback.SendAsync(
                HttpMethod.Delete,
                $"/Videos/ActiveEncodings?deviceId={Uri.EscapeDataString(session.DeviceId)}&playSessionId={session.PlaySessionId}",
                null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Failed to stop Jellyfin encoding for {Session}", session.PlexSessionId);
        }
    }

    private Task Ping(PlexContext ctx)
    {
        if (ctx.Query("session") is { } plexSession)
        {
            _sessions.Touch(plexSession);
        }

        return ctx.WriteEmpty();
    }

    /// <summary>
    /// DASH for Plex Web. Jellyfin only produces HLS, so this runs Jellyfin's ffmpeg directly and
    /// serves a static MPD whose segments are produced on demand (see <see cref="DashJob"/>).
    /// </summary>
    private async Task StartDash(PlexContext ctx)
    {
        var target = ResolveTranscodeTarget(ctx);
        if (target is null || !File.Exists(target.Value.Source.Path))
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var (item, source) = target.Value;
        var plexSession = ctx.Query("session") ?? ctx.Param("X-Plex-Session-Identifier") ?? Guid.NewGuid().ToString("N");
        if (await _sessions.RemoveAsync(plexSession).ConfigureAwait(false) is { Dash: null } previousHls)
        {
            await StopJellyfinEncodingAsync(previousHls).ConfigureAwait(false);
        }

        var options = BuildDashOptions(ctx, item, source);
        var session = _sessions.Create(plexSession, item.Id, source.Id, "plex-" + ctx.ClientIdentifier);
        var dir = Path.Combine(_configurationManager.GetTranscodePath(), "shapeshifter-dash-" + session.PlaySessionId);
        var job = new DashJob(options, _mediaEncoder.EncoderPath, dir, _loggerFactory.CreateLogger<DashJob>());
        session.Dash = job;
        await job.StartAsync().ConfigureAwait(false);

        var video = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var (width, height) = ScaledSize(video?.Width, video?.Height, options.MaxWidth, options.MaxHeight);
        var audio = source.MediaStreams.FirstOrDefault(s => s.Index == options.AudioStreamIndex);
        var prefix = $"{TranscodeBase}/session/{Uri.EscapeDataString(session.PlexSessionId)}/{session.Secret}/";
        _logger.LogInformation("Starting DASH transcode of '{Item}' for {Client} ({Encoder}, {Kbps} kbps)", item.Name, ctx.Product, options.Encoder, options.VideoKbps);

        ctx.Http.Response.ContentType = "application/dash+xml";
        await ctx.Http.Response.WriteAsync(DashManifest.Build(options, prefix, width, height, audio?.Language), ctx.Http.RequestAborted).ConfigureAwait(false);
    }

    private DashOptions BuildDashOptions(PlexContext ctx, BaseItem item, MediaSourceInfo source)
    {
        var data = _userDataManager.GetUserData(ctx.User, item);
        var video = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audioIndex = StreamIndex(ctx.Query("audioStreamID")) ?? data?.AudioStreamIndex;
        var audio = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == audioIndex)
            ?? source.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.IsDefault).FirstOrDefault();

        BurnSubtitle? burn = null;
        var subIndex = StreamIndex(ctx.Query("subtitleStreamID")) ?? data?.SubtitleStreamIndex;
        var subtitle = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == subIndex);
        if (subtitle is not null && ctx.Query("subtitles") != "none" && (ctx.Query("subtitles") == "burn" || !subtitle.IsTextSubtitleStream))
        {
            var relative = source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal).OrderBy(s => s.Index).ToList().IndexOf(subtitle);
            burn = new BurnSubtitle(subtitle.IsExternal ? subtitle.Path : null, Math.Max(0, relative), !subtitle.IsTextSubtitleStream);
        }

        var (maxWidth, maxHeight) = ctx.Query("videoResolution") is { } res && res.Split('x') is [var w, var h]
            && int.TryParse(w, out var wi) && int.TryParse(h, out var hi) ? (wi, hi) : ((int?)null, (int?)null);
        var sourceKbps = (source.Bitrate ?? video?.BitRate ?? 8_000_000) / 1000;
        var kbps = Math.Clamp(Math.Min(ctx.QueryInt("maxVideoBitrate") ?? 20_000, sourceKbps), 300, 40_000);

        var encoding = _configurationManager.GetEncodingOptions();
        var encoder = !encoding.EnableHardwareEncoding ? "libx264" : encoding.HardwareAccelerationType switch
        {
            HardwareAccelerationType.nvenc => "h264_nvenc",
            HardwareAccelerationType.qsv => "h264_qsv",
            HardwareAccelerationType.vaapi => "h264_vaapi",
            HardwareAccelerationType.videotoolbox => "h264_videotoolbox",
            HardwareAccelerationType.amf => "h264_amf",
            _ => "libx264",
        };

        return new DashOptions
        {
            InputPath = source.Path,
            DurationSeconds = (source.RunTimeTicks ?? item.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond,
            OffsetSeconds = ctx.QueryLong("offset") is { } offset and > 0 ? offset : 0,
            VideoKbps = (int)kbps,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            AudioStreamIndex = audio?.Index,
            AudioChannels = Math.Clamp(Math.Min(audio?.Channels ?? 2, ctx.QueryInt("maxAudioChannels") ?? 2), 1, 6),
            Subtitle = burn,
            Encoder = encoder,
            VaapiDevice = encoding.VaapiDevice,
        };
    }

    internal static (int? Width, int? Height) ScaledSize(int? width, int? height, int? maxWidth, int? maxHeight)
    {
        if (width is not > 0 || height is not > 0)
        {
            return (null, null);
        }

        var scale = Math.Min(1.0, Math.Min((maxWidth ?? width.Value) / (double)width.Value, (maxHeight ?? height.Value) / (double)height.Value));
        return ((int)(width.Value * scale) / 2 * 2, (int)(height.Value * scale) / 2 * 2);
    }

    private static async Task ServeDashFile(PlexContext ctx, DashJob job, string rest)
    {
        // {rep}/header or {rep}/{n}.m4s
        var parts = rest.Split('/');
        if (parts.Length != 2 || parts[0] is not (DashManifest.VideoRepresentation or DashManifest.AudioRepresentation))
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var ct = ctx.Http.RequestAborted;
        string? path = parts[1] == "header"
            ? await job.GetHeaderAsync(parts[0], ct).ConfigureAwait(false)
            : parts[1].EndsWith(".m4s", StringComparison.Ordinal) && int.TryParse(parts[1][..^4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? await job.GetSegmentAsync(parts[0], n, ct).ConfigureAwait(false)
                : null;
        if (path is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var contentType = parts[0] == DashManifest.VideoRepresentation ? "video/mp4" : "audio/mp4";
        await Results.File(path, contentType).ExecuteAsync(ctx.Http).ConfigureAwait(false);
    }
}
