using System.Globalization;

namespace ReplayWorkbench.Core;

/// <summary>Formatting shared by the GUI and any other front end.</summary>
public static class Display
{
    /// <summary>h:mm:ss past an hour, mm:ss.mmm below it - same as the web tool.</summary>
    public static string Clock(uint ms)
    {
        var s = ms / 1000;
        var msec = ms % 1000;
        var h = s / 3600;
        s %= 3600;
        var m = s / 60;
        s %= 60;
        return h > 0
            ? $"{h}:{m:00}:{s:00}"
            : $"{m:00}:{s:00}.{msec:000}";
    }

    public static string Bytes(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1048576 => $"{n / 1024.0:0} KB",
        _ => (n / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
    };
}
