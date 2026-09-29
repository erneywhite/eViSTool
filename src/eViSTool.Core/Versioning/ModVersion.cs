using System.Globalization;

namespace eViSTool.Core.Versioning;

/// <summary>
/// Лояльная версия мода/игры. Авторы пишут что угодно: "1.2", "1.2.3.4", "v1.0.0-rc.1", "1.22.0-pre.3".
/// Сравнение — как в semver: числовые части по очереди (недостающие = 0), затем пре-релиз
/// (версия без пре-релиза старше версии с ним).
/// </summary>
public sealed class ModVersion : IComparable<ModVersion>, IEquatable<ModVersion>
{
    private readonly int[] _parts;

    public string Original { get; }
    public string Prerelease { get; }
    public bool IsPrerelease => Prerelease.Length > 0;

    public int Major => Part(0);
    public int Minor => Part(1);
    public int Patch => Part(2);

    private ModVersion(string original, int[] parts, string prerelease)
    {
        Original = original;
        _parts = parts;
        Prerelease = prerelease;
    }

    private int Part(int i) => i < _parts.Length ? _parts[i] : 0;

    public static bool TryParse(string? text, out ModVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];

        // метаданные сборки (+build) в сравнении не участвуют
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var pre = dash >= 0 ? s[(dash + 1)..] : "";

        var pieces = core.Split('.');
        var parts = new int[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
                return false;
        }

        version = new ModVersion(text.Trim(), parts, pre);
        return true;
    }

    public static ModVersion? ParseOrNull(string? text) => TryParse(text, out var v) ? v : null;

    /// <summary>Та же ветка игры (major.minor), например 1.22.3 и 1.22.7.</summary>
    public bool SameBranch(ModVersion other) => Major == other.Major && Minor == other.Minor;

    public int CompareTo(ModVersion? other)
    {
        if (other is null) return 1;

        var n = Math.Max(_parts.Length, other._parts.Length);
        for (var i = 0; i < n; i++)
        {
            var c = Part(i).CompareTo(other.Part(i));
            if (c != 0) return c;
        }

        if (!IsPrerelease && !other.IsPrerelease) return 0;
        if (!IsPrerelease) return 1;
        if (!other.IsPrerelease) return -1;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    // semver: идентификаторы через точку; числа сравниваются как числа и младше строк
    private static int ComparePrerelease(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        var n = Math.Min(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var aNum = int.TryParse(pa[i], out var ai);
            var bNum = int.TryParse(pb[i], out var bi);
            int c;
            if (aNum && bNum) c = ai.CompareTo(bi);
            else if (aNum) c = -1;
            else if (bNum) c = 1;
            else c = string.Compare(pa[i], pb[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    public bool Equals(ModVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => Equals(obj as ModVersion);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease.ToLowerInvariant());
    public override string ToString() => Original;

    public static bool operator <(ModVersion a, ModVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ModVersion a, ModVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ModVersion a, ModVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ModVersion a, ModVersion b) => a.CompareTo(b) >= 0;
}
