namespace Orion.Core.Auth;

/// <summary>Parsing helpers for OAuth redirect query strings.</summary>
public static class OAuthQuery
{
    /// <summary>Parses <c>/?a=b&amp;c=d</c> (or a bare <c>a=b&amp;c=d</c>) into a dictionary.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var question = target.IndexOf('?');
        var query = question >= 0 ? target[(question + 1)..] : target;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;

            var key = Uri.UnescapeDataString(pair[..equals]);
            var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }
}
