using System.Globalization;

namespace PyRuntimeInspector.App.Infrastructure;

public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public bool IsPrerelease => Prerelease is not null;

    public static SemanticVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!TryParse(value, out var version))
            throw new FormatException($"'{value}' is not a valid semantic version.");
        return version;
    }

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrEmpty(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return false;

        var buildSeparator = value.IndexOf('+');
        var versionAndPrerelease = buildSeparator >= 0 ? value[..buildSeparator] : value;
        if (buildSeparator >= 0)
        {
            var build = value[(buildSeparator + 1)..];
            if (!ValidIdentifiers(build, enforceNumericLeadingZeroRule: false)
                || build.Contains('+'))
                return false;
        }

        var prereleaseSeparator = versionAndPrerelease.IndexOf('-');
        var core = prereleaseSeparator >= 0
            ? versionAndPrerelease[..prereleaseSeparator]
            : versionAndPrerelease;
        var prerelease = prereleaseSeparator >= 0
            ? versionAndPrerelease[(prereleaseSeparator + 1)..]
            : null;
        if (prerelease is not null && !ValidIdentifiers(prerelease, enforceNumericLeadingZeroRule: true))
            return false;

        var parts = core.Split('.');
        if (parts.Length != 3
            || !TryParseCoreNumber(parts[0], out var major)
            || !TryParseCoreNumber(parts[1], out var minor)
            || !TryParseCoreNumber(parts[2], out var patch))
            return false;

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
            return 1;

        var coreComparison = Major.CompareTo(other.Major);
        if (coreComparison == 0)
            coreComparison = Minor.CompareTo(other.Minor);
        if (coreComparison == 0)
            coreComparison = Patch.CompareTo(other.Patch);
        if (coreComparison != 0)
            return coreComparison;

        if (Prerelease is null)
            return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null)
            return -1;

        var leftIdentifiers = Prerelease.Split('.');
        var rightIdentifiers = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(leftIdentifiers.Length, rightIdentifiers.Length); index++)
        {
            var identifierComparison = CompareIdentifier(leftIdentifiers[index], rightIdentifiers[index]);
            if (identifierComparison != 0)
                return identifierComparison;
        }
        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion version && Equals(version);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString() => Prerelease is null
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    private static bool TryParseCoreNumber(string value, out int result)
    {
        result = 0;
        return value.Length > 0
            && (value.Length == 1 || value[0] != '0')
            && value.All(IsAsciiDigit)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static bool ValidIdentifiers(string value, bool enforceNumericLeadingZeroRule)
    {
        if (value.Length == 0)
            return false;
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0 || !identifier.All(IsIdentifierCharacter))
                return false;
            if (enforceNumericLeadingZeroRule
                && identifier.Length > 1
                && identifier[0] == '0'
                && identifier.All(IsAsciiDigit))
                return false;
        }
        return true;
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(IsAsciiDigit);
        var rightNumeric = right.All(IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var lengthComparison = left.Length.CompareTo(right.Length);
            return lengthComparison != 0
                ? lengthComparison
                : string.CompareOrdinal(left, right);
        }
        if (leftNumeric)
            return -1;
        if (rightNumeric)
            return 1;
        return string.CompareOrdinal(left, right);
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private static bool IsIdentifierCharacter(char value) =>
        value is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '-';
}
