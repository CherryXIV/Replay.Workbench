namespace ReplayWorkbench.Core;

/// <summary>How a customize byte should be presented and edited.</summary>
public enum CustomizeKind
{
    /// <summary>A plain index or amount, 0-255.</summary>
    Number,
    /// <summary>An index into a race/gender-specific palette in the game's data -
    /// <b>not</b> an RGB value, so it can only be shown as a number here.</summary>
    Color,
    Race,
    Gender,
    Clan,
    /// <summary>A bitmask (facial features).</summary>
    Mask,
}

/// <summary>One byte of the customize block, named for the UI.</summary>
public sealed record CustomizeField(int Index, string Name, CustomizeKind Kind, string Hint = "");

/// <summary>
/// The 26-byte character customize block, as it appears identically in both the
/// PlayerSpawn payload and each party-portrait member block.
///
/// <para>Colour bytes are palette <i>indices</i> into tables that live in the
/// game's own data files, which this tool has no access to. They are therefore
/// editable as numbers only: there is no way to render a faithful swatch without
/// reading the client's chara-make data, and a made-up swatch would be worse than
/// an honest number.</para>
/// </summary>
public sealed class Customize
{
    public const int Length = 26;

    private readonly byte[] _bytes;

    public Customize() => _bytes = new byte[Length];

    public Customize(ReadOnlySpan<byte> src)
    {
        if (src.Length < Length) throw new ArgumentException($"customize needs {Length} bytes", nameof(src));
        _bytes = src[..Length].ToArray();
    }

    public byte this[int index]
    {
        get => _bytes[index];
        set => _bytes[index] = value;
    }

    public byte[] ToArray() => (byte[])_bytes.Clone();
    public ReadOnlySpan<byte> Span => _bytes;
    public Customize Clone() => new(_bytes);
    public bool SameAs(Customize other) => _bytes.AsSpan().SequenceEqual(other._bytes);

    public byte Race { get => _bytes[0]; set => _bytes[0] = value; }
    public byte Gender { get => _bytes[1]; set => _bytes[1] = value; }
    public byte Clan { get => _bytes[4]; set => _bytes[4] = value; }

    public string ToHex() => Convert.ToHexString(_bytes);

    /// <summary>Parse a 26-byte customize block from hex, for copy/paste between characters.</summary>
    public static Customize? FromHex(string text)
    {
        var clean = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (clean.Length != Length * 2) return null;
        try { return new Customize(Convert.FromHexString(clean)); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// The block's fields in byte order.  Names follow the layout the community
    /// has long settled on and which the sample recordings agree with (race/clan
    /// pairs line up, gender is 0/1, the facial-feature byte reads as a mask).
    /// </summary>
    public static readonly IReadOnlyList<CustomizeField> Fields = new[]
    {
        new CustomizeField(0, "Race", CustomizeKind.Race),
        new CustomizeField(1, "Gender", CustomizeKind.Gender),
        new CustomizeField(2, "Body type", CustomizeKind.Number, "1 for player characters"),
        new CustomizeField(3, "Height", CustomizeKind.Number, "0-100"),
        new CustomizeField(4, "Clan", CustomizeKind.Clan),
        new CustomizeField(5, "Face", CustomizeKind.Number, "face index for this clan"),
        new CustomizeField(6, "Hairstyle", CustomizeKind.Number, "hair index for this clan/gender"),
        new CustomizeField(7, "Highlights", CustomizeKind.Number, "0 = off, 128 = on"),
        new CustomizeField(8, "Skin colour", CustomizeKind.Color),
        new CustomizeField(9, "Eye colour (right)", CustomizeKind.Color, "differs from left only with heterochromia"),
        new CustomizeField(10, "Hair colour", CustomizeKind.Color),
        new CustomizeField(11, "Highlight colour", CustomizeKind.Color),
        new CustomizeField(12, "Facial features", CustomizeKind.Mask, "bitmask; bit 7 is the legacy tattoo"),
        new CustomizeField(13, "Feature colour", CustomizeKind.Color),
        new CustomizeField(14, "Eyebrows", CustomizeKind.Number),
        new CustomizeField(15, "Eye colour (left)", CustomizeKind.Color),
        new CustomizeField(16, "Eye shape", CustomizeKind.Number, "bit 7 = small iris"),
        new CustomizeField(17, "Nose", CustomizeKind.Number),
        new CustomizeField(18, "Jaw", CustomizeKind.Number),
        new CustomizeField(19, "Mouth", CustomizeKind.Number, "bit 7 = lip colour on"),
        new CustomizeField(20, "Lip colour", CustomizeKind.Color),
        new CustomizeField(21, "Muscle / tail size", CustomizeKind.Number, "race feature size, 0-100"),
        new CustomizeField(22, "Tail / ear shape", CustomizeKind.Number, "race feature type"),
        new CustomizeField(23, "Bust size", CustomizeKind.Number, "0-100"),
        new CustomizeField(24, "Face paint", CustomizeKind.Number, "bit 7 = mirrored"),
        new CustomizeField(25, "Face paint colour", CustomizeKind.Color),
    };

    public static readonly (byte Id, string Name)[] Races =
    {
        (1, "Hyur"), (2, "Elezen"), (3, "Lalafell"), (4, "Miqo'te"),
        (5, "Roegadyn"), (6, "Au Ra"), (7, "Hrothgar"), (8, "Viera"),
    };

    /// <summary>
    /// Clan ids run two per race in race order, which every sample recording
    /// agrees with (a Lalafell reads clan 5 or 6, a Hrothgar 13 or 14).
    /// </summary>
    public static readonly (byte Id, string Name)[] Clans =
    {
        (1, "Midlander"), (2, "Highlander"),
        (3, "Wildwood"), (4, "Duskwight"),
        (5, "Plainsfolk"), (6, "Dunesfolk"),
        (7, "Seeker of the Sun"), (8, "Keeper of the Moon"),
        (9, "Sea Wolf"), (10, "Hellsguard"),
        (11, "Raen"), (12, "Xaela"),
        (13, "Helions"), (14, "The Lost"),
        (15, "Rava"), (16, "Veena"),
    };

    public static IEnumerable<(byte Id, string Name)> ClansOf(byte race) =>
        race is >= 1 and <= 8 ? Clans.Skip((race - 1) * 2).Take(2) : Clans;

    public static string RaceName(byte race) =>
        Races.FirstOrDefault(r => r.Id == race).Name ?? race.ToString();

    public static string ClanName(byte clan) =>
        Clans.FirstOrDefault(c => c.Id == clan).Name ?? clan.ToString();

    public static string GenderName(byte gender) => gender switch
    {
        0 => "Male",
        1 => "Female",
        _ => gender.ToString(),
    };

    /// <summary>A valid generic customize for (race, gender): default features, mid tones.</summary>
    public static Customize Generic(byte race, byte gender)
    {
        var clan = (byte)((race - 1) * 2 + 1); // first clan of the race
        return new Customize(new byte[]
        {
            race, gender, 1, 50, clan, 1, 1, 0, 128, 1,
            1, 1, 0, 0, 1, 1, 1, 1, 1, 1,
            1, 0, 0, 0, 0, 0,
        });
    }
}
