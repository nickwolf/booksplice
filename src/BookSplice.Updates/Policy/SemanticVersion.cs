namespace BookSplice.Updates.Policy;

public sealed class SemanticVersion
{
    private readonly string[] _prerelease;

    private SemanticVersion(long major, long minor, long patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public long Major { get; }

    public long Minor { get; }

    public long Patch { get; }

    public bool IsPrerelease => _prerelease.Length != 0;

    public static SemanticVersion ParseTag(string tag)
    {
        if (tag is null || tag.Length < 6 || tag[0] != 'v')
        {
            throw new FormatException("A release tag must begin with v and contain a semantic version.");
        }

        var version = tag.AsSpan(1);
        var buildStart = version.IndexOf('+');
        if (buildStart >= 0)
        {
            ValidateIdentifiers(version[(buildStart + 1)..], allowLeadingZeroes: true);
            version = version[..buildStart];
        }

        var prereleaseStart = version.IndexOf('-');
        string[] prerelease = [];
        if (prereleaseStart >= 0)
        {
            prerelease = ValidateIdentifiers(version[(prereleaseStart + 1)..], allowLeadingZeroes: false);
            version = version[..prereleaseStart];
        }

        var core = version.ToString().Split('.');
        if (core.Length != 3)
        {
            throw new FormatException("A semantic version requires major, minor, and patch components.");
        }

        return new SemanticVersion(ParseNumber(core[0]), ParseNumber(core[1]), ParseNumber(core[2]), prerelease);
    }

    public int CompareTo(SemanticVersion? other)
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

        if (!IsPrerelease || !other.IsPrerelease)
        {
            return IsPrerelease.CompareTo(other.IsPrerelease) * -1;
        }

        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var left = _prerelease[i];
            var right = other._prerelease[i];
            var leftNumeric = IsNumeric(left);
            var rightNumeric = IsNumeric(right);
            if (leftNumeric && rightNumeric)
            {
                result = ParseNumber(left).CompareTo(ParseNumber(right));
            }
            else if (leftNumeric != rightNumeric)
            {
                result = leftNumeric ? -1 : 1;
            }
            else
            {
                result = string.CompareOrdinal(left, right);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static string[] ValidateIdentifiers(ReadOnlySpan<char> identifiers, bool allowLeadingZeroes)
    {
        if (identifiers.IsEmpty)
        {
            throw new FormatException("A semantic version identifier cannot be empty.");
        }

        var parts = identifiers.ToString().Split('.');
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                throw new FormatException("A semantic version identifier cannot be empty.");
            }

            foreach (var character in part)
            {
                if (!IsAsciiAlphaNumeric(character) && character != '-')
                {
                    throw new FormatException("A semantic version identifier contains an invalid character.");
                }
            }

            if (!allowLeadingZeroes && IsNumeric(part))
            {
                ParseNumber(part);
            }
        }

        return parts;
    }

    private static long ParseNumber(string value)
    {
        if (value.Length == 0 || (value.Length > 1 && value[0] == '0'))
        {
            throw new FormatException("A numeric semantic version identifier is empty or has a leading zero.");
        }

        long result = 0;
        foreach (var character in value)
        {
            if (character is < '0' or > '9' || result > (long.MaxValue - (character - '0')) / 10)
            {
                throw new FormatException("A numeric semantic version identifier is invalid or too large.");
            }

            result = result * 10 + (character - '0');
        }

        return result;
    }

    private static bool IsNumeric(string value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiAlphaNumeric(char character) =>
        character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
