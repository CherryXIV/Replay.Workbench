using System.Buffers.Binary;

namespace ReplayWorkbench.Updater;

/// <summary>
/// Just enough of the .dat format to read a build number and an opcode histogram
/// out of a recording. Deliberately not a reference to Replay.Workbench.Core —
/// this library must not hold an assembly the update is about to rebuild.
/// </summary>
public static class ReplayProbe
{
    private const int HeaderSize = 0x68;
    private const int ChapterArray = 0x4 + 0xC * 64;
    private const int DataStart = HeaderSize + ChapterArray; // 0x36C
    private const int SegHeader = 12;
    private const int OffBuild = 0x10;
    private const int OffReplayLen = 0x48;
    private static ReadOnlySpan<byte> Magic => "FFXIVREPLAY\0"u8;

    public sealed record Probe(int Build, Dictionary<int, int> Histogram, long Packets);

    public static Probe Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < DataStart || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new FatalException($"{path} is not an FFXIVREPLAY .dat (bad header magic).");

        var span = data.AsSpan();
        var build = BinaryPrimitives.ReadInt32LittleEndian(span[OffBuild..]);
        var replayLen = BinaryPrimitives.ReadInt32LittleEndian(span[OffReplayLen..]);

        // A recording the game never finalised has 0 at 0x48 even though the packets
        // are all there: the length is written back on exit, so a crash leaves it
        // unset. Walk to EOF in that case rather than refusing the file.
        var unfinalised = replayLen <= 0;
        if (!unfinalised && DataStart + (long)replayLen > data.Length)
            throw new FatalException(
                $"Replay length at 0x48 ({replayLen}) runs past the end of the file " +
                $"({data.Length - DataStart} bytes of data available).");
        if (unfinalised) replayLen = data.Length - DataStart;

        var hist = new Dictionary<int, int>();
        long packets = 0;
        var off = 0;
        while (off < replayLen)
        {
            var b = DataStart + off;
            if (b + SegHeader > data.Length) break;
            var length = BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
            if (b + SegHeader + length > data.Length) break; // trailing partial write
            var op = BinaryPrimitives.ReadUInt16LittleEndian(span[b..]);
            hist[op] = hist.GetValueOrDefault(op) + 1;
            packets++;
            off += SegHeader + length;
        }
        return new Probe(build, hist, packets);
    }

    /// <summary>
    /// Does the recording's own opcode set actually fit the patch we just added?
    ///
    /// <para>A recording made on the new client should be 100% accounted for by the
    /// new patch's vtable and by nothing else. Anything less means the chain or the
    /// diff is wrong, and it is far better to hear that now than after exporting a
    /// replay that crashes the client.</para>
    /// </summary>
    public static void ConfirmAgainstPatch(
        Dictionary<int, int> hist, string diffsDir, string patch, Action<string> log)
    {
        var universe = new HashSet<int>(DiffHop.Load(diffsDir, patch).Map.Values);
        var ipc = hist.Where(kv => kv.Key < NameCarrier.NonIpcOpcode).ToList();
        if (ipc.Count == 0)
        {
            log("recording has no IPC packets to check against");
            return;
        }
        var kinds = ipc.Count(kv => universe.Contains(kv.Key));
        long packets = ipc.Where(kv => universe.Contains(kv.Key)).Sum(kv => (long)kv.Value);
        long total = ipc.Sum(kv => (long)kv.Value);
        log($"recording vs {patch}: {kinds}/{ipc.Count} opcode kinds " +
            $"({100.0 * packets / total:F1}% of packets) are in its vtable");
        if (kinds == ipc.Count) return;

        var strays = ipc.Where(kv => !universe.Contains(kv.Key)).Select(kv => kv.Key)
            .OrderBy(x => x).Take(12);
        log("  unaccounted opcodes: " + string.Join(", ", strays.Select(o => $"0x{o:x}")));
        log($"  a fresh {patch} recording should be 100% -- check the build number and the diff");
    }
}
