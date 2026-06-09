namespace SelfUpdate;

/// <summary>
/// A small semantic-version implementation: <c>major.minor.patch</c> with an
/// optional <c>-prerelease</c> and <c>+build</c> suffix. Comparison follows the
/// SemVer 2.0 precedence rules closely enough for update decisions: build
/// metadata is ignored, and a prerelease sorts lower than its release.
/// </summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? Prerelease = null)
    : IComparable<SemVer>
{
    public static bool TryParse(string? value, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var span = value.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
            span = span[1..];

        // Strip build metadata (everything after '+').
        var plus = span.IndexOf('+');
        if (plus >= 0)
            span = span[..plus];

        // Split off the prerelease (everything after the first '-').
        string? prerelease = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = span[(dash + 1)..].ToString();
            span = span[..dash];
            if (prerelease.Length == 0)
                return false;
        }

        Span<Range> parts = stackalloc Range[4];
        var count = span.Split(parts, '.');
        if (count is < 1 or > 3)
            return false;

        int major = 0, minor = 0, patch = 0;
        for (var i = 0; i < count; i++)
        {
            if (!int.TryParse(span[parts[i]], out var n) || n < 0)
                return false;
            switch (i)
            {
                case 0: major = n; break;
                case 1: minor = n; break;
                case 2: patch = n; break;
            }
        }

        version = new SemVer(major, minor, patch, prerelease);
        return true;
    }

    public static SemVer Parse(string value) =>
        TryParse(value, out var v) ? v : throw new FormatException($"Invalid version: '{value}'.");

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    private static int ComparePrerelease(string? a, string? b)
    {
        // A version with no prerelease has higher precedence than one with a prerelease.
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;

        var ai = a.Split('.');
        var bi = b.Split('.');
        var len = Math.Min(ai.Length, bi.Length);
        for (var i = 0; i < len; i++)
        {
            var c = ComparePrereleaseIdentifier(ai[i], bi[i]);
            if (c != 0) return c;
        }
        return ai.Length.CompareTo(bi.Length);
    }

    private static int ComparePrereleaseIdentifier(string a, string b)
    {
        var aNum = int.TryParse(a, out var an);
        var bNum = int.TryParse(b, out var bn);
        if (aNum && bNum) return an.CompareTo(bn);
        if (aNum) return -1;  // numeric identifiers sort lower than alphanumeric
        if (bNum) return 1;
        return string.CompareOrdinal(a, b);
    }

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        return Prerelease is null ? core : $"{core}-{Prerelease}";
    }

    public bool IsPrerelease => Prerelease is not null;
}
