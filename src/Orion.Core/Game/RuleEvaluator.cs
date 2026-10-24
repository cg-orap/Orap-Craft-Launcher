using System.Text.RegularExpressions;

namespace Orion.Core.Game;

/// <summary>
/// Describes the environment that "rules" arrays are evaluated against
/// (the <c>rules</c> field of libraries and of argument nodes).
/// </summary>
public sealed class RuleContext
{
    public required string OsName { get; init; }
    public string? OsVersion { get; init; }
    public string? OsArch { get; init; }
    public IReadOnlyDictionary<string, bool> Features { get; init; } = EmptyFeatures;

    private static readonly Dictionary<string, bool> EmptyFeatures = [];

    /// <summary>Builds the context for the machine the launcher is running on.</summary>
    public static RuleContext ForCurrentMachine(IReadOnlyDictionary<string, bool>? features = null)
        => new()
        {
            OsName = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux",
            OsVersion = Environment.OSVersion.Version.ToString(),
            OsArch = RuntimeArchitecture(),
            Features = features ?? EmptyFeatures,
        };

    public static string RuntimeArchitecture() => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.Arm => "arm32",
        _ => "x86_64",
    };

    public RuleContext WithFeatures(IReadOnlyDictionary<string, bool> features) => new()
    {
        OsName = OsName,
        OsVersion = OsVersion,
        OsArch = OsArch,
        Features = features,
    };
}

/// <summary>
/// Evaluates Mojang's allow/deny "rules" arrays.
///
/// The documented algorithm is sequential: processing starts with <c>false</c> and every
/// rule whose conditions match overwrites the result. A rule whose conditions do not
/// match is skipped entirely, so a trailing "deny" rule can veto earlier allows.
/// </summary>
public static class RuleEvaluator
{
    public static bool IsAllowed(IReadOnlyList<Json.Rule>? rules, RuleContext context)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, context)) continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool Matches(Json.Rule rule, RuleContext context)
    {
        if (rule.Os is not null)
        {
            var os = rule.Os;

            if (!string.IsNullOrEmpty(os.Name) && !string.Equals(os.Name, context.OsName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(os.Arch) && !string.Equals(os.Arch, context.OsArch, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(os.Version))
            {
                try
                {
                    if (!Regex.IsMatch(context.OsVersion ?? "", os.Version, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                        return false;
                }
                catch (ArgumentException)
                {
                    // A malformed rule must not crash a launch; treat it as "does not match".
                    return false;
                }
            }
        }

        if (rule.Features is not null)
        {
            foreach (var (feature, expected) in rule.Features)
            {
                var actual = context.Features.TryGetValue(feature, out var value) && value;
                if (actual != expected) return false;
            }
        }

        return true;
    }
}
