namespace Rxdk.Engine.Bootstrap;

/// <summary>
/// Lenient dotted-numeric version compare (tolerates a leading 'v' and a -prerelease/+build suffix).
/// Used only for the extension-compatibility gate, so it FAILS SAFE: if either input can't be parsed
/// it never reports "newer", so a garbled version can't wrongly block a component update.
/// </summary>
public static class RxdkSemver
{
    public static bool TryParse(string? s, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s!.Trim().TrimStart('v', 'V');
        // Keep only the leading dotted-numeric core (drop any -prerelease/+build tail).
        int i = 0;
        while (i < t.Length && (char.IsDigit(t[i]) || t[i] == '.')) i++;
        t = t.Substring(0, i).Trim('.');
        if (t.Length == 0) return false;
        if (!t.Contains('.')) t += ".0"; // System.Version needs at least major.minor
        return Version.TryParse(t, out version!);
    }

    /// <summary>True only when both parse and <paramref name="a"/> is strictly newer than <paramref name="b"/>.</summary>
    public static bool IsNewer(string? a, string? b) =>
        TryParse(a, out var va) && TryParse(b, out var vb) && va > vb;
}
