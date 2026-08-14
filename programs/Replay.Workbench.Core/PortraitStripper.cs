using System.Buffers.Binary;

namespace ReplayWorkbench.Core;

/// <summary>Result of stripping packets out of a finished export.</summary>
public sealed class StripResult
{
    public required byte[] Bytes { get; init; }
    public required string Note { get; init; }
}

/// <summary>
/// Physically remove every PartyPortraitInfo packet from the data stream and fix
/// up the replay length + chapter offsets.
///
/// <para>Must run before transpose, while packets still carry the file's own
/// opcodes.</para>
/// </summary>
public static class PortraitStripper
{
    /// <summary>True when the file's patch has a PartyPortraitInfo entry to find.</summary>
    public static bool IsAvailable(string? filePatch) =>
        PatchChain.Lookup(filePatch, "PartyPortraitInfo") is not null;

    public static StripResult Strip(byte[] bytes, string? filePatch)
    {
        var op = PatchChain.Lookup(filePatch, "PartyPortraitInfo");
        if (op is null) return new StripResult { Bytes = bytes, Note = "" };

        var span = bytes.AsSpan();
        var replayLen = BinaryPrimitives.ReadInt32LittleEndian(span[ReplayFormat.OffReplayLen..]);

        // Find every portrait segment by data-stream offset (relative to DataStart).
        var removed = new List<(int At, int Total)>();
        var off = 0;
        while (off < replayLen)
        {
            var b = ReplayFormat.DataStart + off;
            var total = ReplayFormat.SegHeader + BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
            if (BinaryPrimitives.ReadUInt16LittleEndian(span[b..]) == op) removed.Add((off, total));
            off += total;
        }
        if (removed.Count == 0)
            return new StripResult { Bytes = bytes, Note = " · no portrait packets to strip" };

        var removedBytes = removed.Sum(r => r.Total);

        // Rebuild: header + chapter array unchanged, body minus the portrait
        // segments, plus any trailing bytes after the data area.
        var outp = new byte[bytes.Length - removedBytes];
        bytes.AsSpan(0, ReplayFormat.DataStart).CopyTo(outp);
        var w = ReplayFormat.DataStart;
        off = 0;
        while (off < replayLen)
        {
            var b = ReplayFormat.DataStart + off;
            var total = ReplayFormat.SegHeader + BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
            if (BinaryPrimitives.ReadUInt16LittleEndian(span[b..]) != op)
            {
                bytes.AsSpan(b, total).CopyTo(outp.AsSpan(w));
                w += total;
            }
            off += total;
        }
        bytes.AsSpan(ReplayFormat.DataStart + replayLen).CopyTo(outp.AsSpan(w)); // trailing bytes, if any

        var ov = outp.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(ov[ReplayFormat.OffReplayLen..], replayLen - removedBytes);

        // Each chapter offset must drop by the bytes removed strictly before it.
        var clen = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(ov[ReplayFormat.HeaderSize..]),
            0, ReplayFormat.MaxChapters);
        for (var i = 0; i < clen; i++)
        {
            var e = ReplayFormat.HeaderSize + 4 + i * ReplayFormat.ChapterEntry;
            var choff = BinaryPrimitives.ReadUInt32LittleEndian(ov[(e + 4)..]);
            var shift = removed.Where(r => r.At < choff).Sum(r => r.Total);
            BinaryPrimitives.WriteUInt32LittleEndian(ov[(e + 4)..], choff - (uint)shift);
        }

        return new StripResult
        {
            Bytes = outp,
            Note = $" · stripped {removed.Count} portrait packet{(removed.Count > 1 ? "s" : "")}",
        };
    }
}
