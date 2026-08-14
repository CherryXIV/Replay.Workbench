using System.Text.RegularExpressions;

namespace ReplayWorkbench.Updater;

/// <summary>Something the update cannot recover from; the message is for the user.</summary>
public sealed class FatalException : Exception
{
    public FatalException(string message) : base(message) { }
}

/// <summary>
/// Ordering for patch names: 7.55 &lt; 7.55h &lt; 7.55h2 &lt; 7.76 &lt; 8.00.
///
/// <para>opcodediff writes the minor with however many digits the patch had a
/// name for — 6.3 and 6.30h are the same patch line — so the minor is padded to
/// two digits before comparing. Read as a plain integer, "7.56" would sort below
/// "7.55" and a whole patch would be skipped without a word.</para>
/// </summary>
public readonly record struct PatchVersion(int Major, int Minor, string Suffix, int Number)
    : IComparable<PatchVersion>
{
    private static readonly Regex Shape = new(@"^(\d+)\.(\d+)([a-z]*)(\d*)$", RegexOptions.Compiled);

    public static PatchVersion Of(string version)
    {
        var m = Shape.Match(version);
        if (!m.Success) return new PatchVersion(99, 99, "zz", 99);
        var major = int.Parse(m.Groups[1].Value);
        var minor = int.Parse(m.Groups[2].Value.PadRight(2, '0'));
        var suffix = m.Groups[3].Value;
        var digits = m.Groups[4].Value;
        var number = digits.Length > 0 ? int.Parse(digits) : (suffix.Length > 0 ? 1 : 0);
        return new PatchVersion(major, minor, suffix, number);
    }

    public int CompareTo(PatchVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = string.CompareOrdinal(Suffix, other.Suffix);
        return c != 0 ? c : Number.CompareTo(other.Number);
    }

    public static int Compare(string a, string b) => Of(a).CompareTo(Of(b));

    /// <summary>Sort patch names oldest first.</summary>
    public static List<string> Sorted(IEnumerable<string> versions) =>
        versions.OrderBy(Of).ToList();

    public static string Newest(IEnumerable<string> versions) =>
        versions.OrderBy(Of).Last();
}
