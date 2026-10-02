using System.Globalization;

namespace Shared.Consistency;

/// <summary>A Postgres write-ahead-log position ("16/B374D848"): how far a server has written or replayed.</summary>
public static class Lsn
{
    public const string Header = "X-Consistency-Token";

    /// <summary>Parses the text form. Anything that is not exactly <c>HEX/HEX</c> is not a position (the value arrives from a client).</summary>
    public static bool TryParse(string? text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 17)
        {
            return false;
        }

        var parts = text.Split('/');
        if (parts.Length != 2
            || parts[0].Length is 0 or > 8 || parts[1].Length is 0 or > 8
            || !uint.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var high)
            || !uint.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low))
        {
            return false;
        }

        value = ((ulong)high << 32) | low;
        return true;
    }

    public static string Format(ulong value) => $"{value >> 32:X}/{value & 0xFFFFFFFF:X}";
}
