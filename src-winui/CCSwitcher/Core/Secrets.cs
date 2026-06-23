// Secret sanitization for user-facing error messages.
//
// Port of sanitize_secrets() from src-tauri/src/commands.rs.
//
// Applied to ALL strings shown to the user so that raw tokens, OAuth blobs,
// and Bearer credentials never leak through error notifications.

using System.Text.RegularExpressions;

namespace CCSwitcher.Core;

/// <summary>
/// Helpers for redacting secrets from strings before they reach the UI.
/// </summary>
public static partial class Secrets
{
    // -----------------------------------------------------------------------
    // Pre-compiled regex patterns (source-generated for performance)
    // -----------------------------------------------------------------------

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_-]+", RegexOptions.Compiled)]
    private static partial Regex SkAntPattern();

    // Negative lookahead ensures sk-ant-* tokens are handled exclusively by
    // SkAntPattern and not double-replaced by this pattern.
    [GeneratedRegex(@"sk-(?!ant-)[A-Za-z0-9_-]+", RegexOptions.Compiled)]
    private static partial Regex SkPattern();

    [GeneratedRegex(@"Bearer [A-Za-z0-9._\-/=]+", RegexOptions.Compiled)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"""accessToken""\s*:\s*""[^""]*""", RegexOptions.Compiled)]
    private static partial Regex AccessTokenPattern();

    [GeneratedRegex(@"""refreshToken""\s*:\s*""[^""]*""", RegexOptions.Compiled)]
    private static partial Regex RefreshTokenPattern();

    [GeneratedRegex(@"""idToken""\s*:\s*""[^""]*""", RegexOptions.Compiled)]
    private static partial Regex IdTokenPattern();

    /// <summary>
    /// Redact secrets from <paramref name="input"/> so they never appear in UI
    /// error messages or notifications.
    /// <para>
    /// Rules (applied in order):
    /// <list type="number">
    ///   <item><c>sk-ant-[A-Za-z0-9_-]+</c> → <c>sk-ant-***</c></item>
    ///   <item><c>sk-[A-Za-z0-9_-]+</c> → <c>sk-***</c></item>
    ///   <item><c>Bearer &lt;token&gt;</c> → <c>Bearer ***</c></item>
    ///   <item>JSON field <c>accessToken</c> value → <c>"accessToken": "***"</c></item>
    ///   <item>JSON field <c>refreshToken</c> value → <c>"refreshToken": "***"</c></item>
    ///   <item>JSON field <c>idToken</c> value → <c>"idToken": "***"</c></item>
    /// </list>
    /// </para>
    /// </summary>
    public static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Order matters: sk-ant-* must be replaced before sk-* so we do not
        // accidentally emit "sk-***-…" with a partial suffix.
        var result = SkAntPattern().Replace(input, "sk-ant-***");
        result = SkPattern().Replace(result, "sk-***");
        result = BearerPattern().Replace(result, "Bearer ***");

        // Redact known OAuth JSON credential fields.
        result = AccessTokenPattern().Replace(result, @"""accessToken"": ""***""");
        result = RefreshTokenPattern().Replace(result, @"""refreshToken"": ""***""");
        result = IdTokenPattern().Replace(result, @"""idToken"": ""***""");

        return result;
    }
}
