using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubZeroDev.WindowManager.Protocol;

/// <summary>A semantic version, <c>major.minor.patch[-prerelease]</c>. Build metadata is ignored.</summary>
[JsonConverter(typeof(SemVersionJsonConverter))]
public sealed record SemVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<SemVersion>
{
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = new SemVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim();
        var plus = core.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            core = core[..plus];
        }

        string? prerelease = null;
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = core[(dash + 1)..];
            core = core[..dash];
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        var parts = core.Split('.');
        if (parts.Length != 3 || !TryPart(parts[0], out var major) || !TryPart(parts[1], out var minor) || !TryPart(parts[2], out var patch))
        {
            return false;
        }

        version = new SemVersion(major, minor, patch, prerelease);
        return true;
    }

    public static SemVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a semantic version.");

    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        if (result != 0)
        {
            return result;
        }

        // A pre-release sorts before its release.
        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(Prerelease, other.Prerelease),
        };
    }

    public static bool operator <(SemVersion left, SemVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemVersion left, SemVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemVersion left, SemVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemVersion left, SemVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (Prerelease is null ? string.Empty : "-" + Prerelease);

    internal static bool TryPart(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// A version range: <c>^1.2</c> (same major, at least 1.2.0; for 0.x the same minor), <c>~1.2</c> (same
/// minor), <c>&gt;=1.2</c>, or an exact version. Missing minor or patch parts read as 0.
/// </summary>
public sealed record VersionRange(string Text, SemVersion Floor, SemVersion? Ceiling)
{
    public bool IsSatisfiedBy(SemVersion version) =>
        version >= Floor && (Ceiling is null || version < Ceiling);

    public static bool TryParse(string? text, out VersionRange range)
    {
        range = new VersionRange(string.Empty, new SemVersion(0, 0, 0), null);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        string op;
        string rest;
        if (trimmed.StartsWith(">=", StringComparison.Ordinal))
        {
            (op, rest) = (">=", trimmed[2..]);
        }
        else if (trimmed[0] is '^' or '~')
        {
            (op, rest) = (trimmed[..1], trimmed[1..]);
        }
        else
        {
            if (!SemVersion.TryParse(trimmed, out var exact))
            {
                return false;
            }

            range = new VersionRange(trimmed, exact, exact with { Patch = exact.Patch + 1, Prerelease = null });
            return true;
        }

        var parts = rest.Trim().Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!SemVersion.TryPart(parts[i], out numbers[i]))
            {
                return false;
            }
        }

        var floor = new SemVersion(numbers[0], numbers[1], numbers[2]);
        SemVersion? ceiling = op switch
        {
            "^" when floor.Major > 0 => new SemVersion(floor.Major + 1, 0, 0),
            "^" => new SemVersion(0, floor.Minor + 1, 0),
            "~" => new SemVersion(floor.Major, floor.Minor + 1, 0),
            _ => null,
        };
        range = new VersionRange(trimmed, floor, ceiling);
        return true;
    }

    public override string ToString() => Text;
}

internal sealed class SemVersionJsonConverter : JsonConverter<SemVersion>
{
    public override SemVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SemVersion.TryParse(reader.GetString(), out var version) ? version : throw new JsonException("Not a semantic version.");

    public override void Write(Utf8JsonWriter writer, SemVersion value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
