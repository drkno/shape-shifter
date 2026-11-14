using System.Buffers.Binary;

namespace Jellyfin.Plugin.ShapeShifter.Playback.Dash;

/// <summary>
/// Reads and shifts decode times in fragmented MP4. ffmpeg's dash muxer restarts every run's
/// timeline at zero (and -output_ts_offset doesn't survive into tfdt), so segments from a run
/// started mid-file are moved to their place in the presentation here, as they're served.
/// </summary>
public static class Fmp4Timestamps
{
    /// <summary>Media timescale from an init segment's mdhd, or null if it can't be found.</summary>
    public static uint? ReadTimescale(ReadOnlySpan<byte> init)
    {
        foreach (var (type, start, size, header) in Boxes(init, 0, init.Length))
        {
            if (type == "moov")
            {
                return FindTimescale(init, start + header, start + size);
            }
        }

        return null;
    }

    /// <summary>First track fragment decode time in a media segment, or null.</summary>
    public static ulong? ReadDecodeTime(ReadOnlySpan<byte> segment)
    {
        foreach (var (type, start, size, header) in Boxes(segment, 0, segment.Length))
        {
            if (type != "moof")
            {
                continue;
            }

            foreach (var (t, s, z, h) in Boxes(segment, start + header, start + size))
            {
                if (t != "traf")
                {
                    continue;
                }

                foreach (var (tt, ss, _, hh) in Boxes(segment, s + h, s + z))
                {
                    if (tt == "tfdt")
                    {
                        var p = ss + hh;
                        return segment[p] == 1
                            ? BinaryPrimitives.ReadUInt64BigEndian(segment[(p + 4)..])
                            : BinaryPrimitives.ReadUInt32BigEndian(segment[(p + 4)..]);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> (in the track's timescale) to every tfdt, and to the
    /// earliest presentation time of any sidx (in the sidx's own timescale, scaled from
    /// <paramref name="timescale"/>). Returns false if a 32-bit field would overflow or underflow.
    /// </summary>
    public static bool Shift(Span<byte> segment, long delta, uint timescale)
    {
        if (delta == 0)
        {
            return true;
        }

        foreach (var (type, start, size, header) in Boxes(segment, 0, segment.Length))
        {
            if (type == "sidx")
            {
                var p = start + header;
                var version = segment[p];
                var sidxScale = BinaryPrimitives.ReadUInt32BigEndian(segment[(p + 8)..]);
                var sidxDelta = timescale == 0 ? 0 : delta * sidxScale / timescale;
                if (!ShiftField(segment[(p + 12)..], version == 1, sidxDelta))
                {
                    return false;
                }
            }
            else if (type == "moof")
            {
                foreach (var (t, s, z, h) in Boxes(segment, start + header, start + size))
                {
                    if (t != "traf")
                    {
                        continue;
                    }

                    foreach (var (tt, ss, _, hh) in Boxes(segment, s + h, s + z))
                    {
                        if (tt == "tfdt")
                        {
                            var p = ss + hh;
                            if (!ShiftField(segment[(p + 4)..], segment[p] == 1, delta))
                            {
                                return false;
                            }
                        }
                    }
                }
            }
        }

        return true;
    }

    private static bool ShiftField(Span<byte> field, bool wide, long delta)
    {
        if (wide)
        {
            var v = (long)BinaryPrimitives.ReadUInt64BigEndian(field) + delta;
            if (v < 0)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt64BigEndian(field, (ulong)v);
            return true;
        }

        var n = BinaryPrimitives.ReadUInt32BigEndian(field) + delta;
        if (n < 0 || n > uint.MaxValue)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32BigEndian(field, (uint)n);
        return true;
    }

    private static uint? FindTimescale(ReadOnlySpan<byte> data, int from, int to)
    {
        foreach (var (type, start, size, header) in Boxes(data, from, to))
        {
            switch (type)
            {
                case "trak" or "mdia":
                    if (FindTimescale(data, start + header, start + size) is { } found)
                    {
                        return found;
                    }

                    break;
                case "mdhd":
                    var p = start + header;
                    return BinaryPrimitives.ReadUInt32BigEndian(data[(p + (data[p] == 1 ? 20 : 12))..]);
            }
        }

        return null;
    }

    private static List<(string Type, int Start, int Size, int Header)> Boxes(ReadOnlySpan<byte> data, int from, int to)
    {
        var boxes = new List<(string, int, int, int)>();
        var pos = from;
        while (pos + 8 <= to)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            var header = 8;
            if (size == 1 && pos + 16 <= to)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(pos + 8)..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = to - pos;
            }

            if (size < header || pos + size > to)
            {
                break;
            }

            boxes.Add((System.Text.Encoding.ASCII.GetString(data.Slice(pos + 4, 4)), pos, (int)size, header));
            pos += (int)size;
        }

        return boxes;
    }
}
