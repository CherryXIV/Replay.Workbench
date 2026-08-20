namespace ReplayWorkbench.Core;

/// <summary>
/// The status icon shown beside a character's name - Busy, Role-playing, the
/// mentor crowns - as the game's <c>OnlineStatus</c> sheet numbers them.
///
/// <para>The ids are the sheet's own row ids, used raw: measured against ten
/// recordings that differ only in the status the recorder had set, the byte in the
/// spawn packet reads 12 for Busy, 22 for Role-playing, 23 for Looking for Party
/// and 27-30 for the four mentor crowns - each exactly its sheet row, with no
/// offset. The load-in transient the recordings all show reads 15, Viewing
/// Cutscene, which is the same story from a value nobody set by hand.</para>
///
/// <para>The table is inlined rather than embedded as generated JSON like the
/// opcode data, because unlike opcodes it does not move with the game's patches -
/// it is a fixed sheet, and the recordings this tool reads span one expansion.
/// <c>tools/OnlineStatus.csv</c> is the sheet it was taken from.</para>
/// </summary>
public static class OnlineStatusData
{
    /// <summary>
    /// What a player in a duty reads, and so the value the anonymizer writes.
    ///
    /// <para>It is the honest answer rather than a blank: a recording only exists
    /// because someone was in a duty, so this is what their icon would have been
    /// had they set no status at all. Zeroing the field instead would leave every
    /// anonymized player reading a value the game gives NPCs.</para>
    /// </summary>
    public const byte InDuty = 43;

    /// <summary>Every row of the sheet that names something, in sheet order.</summary>
    public static readonly IReadOnlyList<(byte Id, string Name)> All = new (byte, string)[]
    {
        (0, "(none)"),
        (1, "Game QA"),
        (2, "Game Master"),
        (3, "Game Master"),
        (4, "Event Participant"),
        (5, "Disconnected"),
        (6, "Waiting for Friend List Approval"),
        (7, "Waiting for Linkshell Approval"),
        (8, "Waiting for Free Company Approval"),
        (9, "Not Found"),
        (10, "Offline"),
        (11, "Battle Mentor"),
        (12, "Busy"),
        (13, "PvP"),
        (14, "Playing Triple Triad"),
        (15, "Viewing Cutscene"),
        (16, "Using a Chocobo Porter"),
        (17, "Away from Keyboard"),
        (18, "Camera Mode"),
        (19, "Looking for Repairs"),
        (20, "Looking to Repair"),
        (21, "Looking to Meld Materia"),
        (22, "Role-playing"),
        (23, "Looking for Party"),
        (24, "Sword for Hire"),
        (25, "Waiting for Duty Finder"),
        (26, "Recruiting Party Members"),
        (27, "Mentor"),
        (28, "PvE Mentor"),
        (29, "Trade Mentor"),
        (30, "PvP Mentor"),
        (31, "Returner"),
        (32, "New Adventurer"),
        (33, "Alliance Leader"),
        (34, "Alliance Party Leader"),
        (35, "Alliance Party Member"),
        (36, "Party Leader"),
        (37, "Party Member"),
        (38, "Party Leader (Cross-world)"),
        (39, "Party Member (Cross-world)"),
        (40, "Another World"),
        (41, "Sharing Duty"),
        (42, "Similar Duty"),
        (43, "In Duty"),
        (44, "Trial Adventurer"),
        (45, "Free Company"),
        (46, "Grand Company"),
        (47, "Online"),
    };

    private static readonly Dictionary<byte, string> Names = All.ToDictionary(e => e.Id, e => e.Name);

    /// <summary>The sheet's name for an id, or a bare number for one it has no row
    /// for - a recording is free to carry a value this sheet has never heard of, and
    /// hiding it would be worse than showing it.</summary>
    public static string NameOf(byte id) => Names.TryGetValue(id, out var n) ? n : $"unknown ({id})";
}
