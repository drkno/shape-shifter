using System.Globalization;
using System.Xml.Linq;

namespace Jellyfin.Plugin.ShapeShifter.Playback.Dash;

/// <summary>
/// The static MPD handed to Plex Web. It's written up front from the known duration rather than
/// taken from ffmpeg (whose manifest is a growing "dynamic" one while encoding), so the player
/// sees a seekable VOD presentation and segments are produced on demand behind it.
/// </summary>
public static class DashManifest
{
    private static readonly XNamespace Ns = "urn:mpeg:dash:schema:mpd:2011";

    public const string VideoRepresentation = "0";
    public const string AudioRepresentation = "1";

    /// <param name="urlPrefix">Absolute path prefix for segments, ending in '/': segments are {prefix}{rep}/{n}.m4s.</param>
    public static string Build(DashOptions o, string urlPrefix, int? width, int? height, string? audioLanguage)
    {
        var duration = Iso(o.PresentationSeconds);
        var period = new XElement(Ns + "Period", new XAttribute("id", "0"), new XAttribute("start", "PT0S"));

        period.Add(new XElement(
            Ns + "AdaptationSet",
            new XAttribute("id", "0"),
            new XAttribute("contentType", "video"),
            new XAttribute("mimeType", "video/mp4"),
            new XAttribute("segmentAlignment", "true"),
            new XAttribute("startWithSAP", "1"),
            Template(urlPrefix, VideoRepresentation),
            new XElement(
                Ns + "Representation",
                new XAttribute("id", VideoRepresentation),
                new XAttribute("codecs", "avc1.640029"),
                new XAttribute("bandwidth", (o.VideoKbps * 1000L).ToString(CultureInfo.InvariantCulture)),
                width is > 0 ? new XAttribute("width", width.Value) : null,
                height is > 0 ? new XAttribute("height", height.Value) : null)));

        if (o.AudioStreamIndex is not null)
        {
            period.Add(new XElement(
                Ns + "AdaptationSet",
                new XAttribute("id", "1"),
                new XAttribute("contentType", "audio"),
                new XAttribute("mimeType", "audio/mp4"),
                new XAttribute("segmentAlignment", "true"),
                new XAttribute("startWithSAP", "1"),
                string.IsNullOrEmpty(audioLanguage) ? null : new XAttribute("lang", audioLanguage),
                Template(urlPrefix, AudioRepresentation),
                new XElement(
                    Ns + "Representation",
                    new XAttribute("id", AudioRepresentation),
                    new XAttribute("codecs", "mp4a.40.2"),
                    new XAttribute("bandwidth", (o.AudioKbps * 1000L).ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("audioSamplingRate", "48000"),
                    new XElement(
                        Ns + "AudioChannelConfiguration",
                        new XAttribute("schemeIdUri", "urn:mpeg:dash:23003:3:audio_channel_configuration:2011"),
                        new XAttribute("value", o.AudioChannels)))));
        }

        var mpd = new XElement(
            Ns + "MPD",
            new XAttribute("profiles", "urn:mpeg:dash:profile:isoff-live:2011"),
            new XAttribute("type", "static"),
            new XAttribute("mediaPresentationDuration", duration),
            new XAttribute("minBufferTime", Iso(DashOptions.SegmentSeconds)),
            new XAttribute("maxSegmentDuration", Iso(DashOptions.SegmentSeconds)),
            period);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), mpd).Declaration + "\n" + mpd;
    }

    private static XElement Template(string prefix, string rep) => new(
        Ns + "SegmentTemplate",
        new XAttribute("timescale", "1000"),
        new XAttribute("duration", DashOptions.SegmentSeconds * 1000),
        new XAttribute("startNumber", "0"),
        new XAttribute("initialization", $"{prefix}{rep}/header"),
        new XAttribute("media", $"{prefix}{rep}/$Number$.m4s"));

    private static string Iso(double seconds) => "PT" + seconds.ToString("0.###", CultureInfo.InvariantCulture) + "S";
}
