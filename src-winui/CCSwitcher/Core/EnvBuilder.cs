// Per-account env builder — port of env_builder.rs.
//
// Given an Account, its (optional) secret, and the global ProxySettings,
// this module produces the exact set of env key/value pairs ccswitcher will
// inject into settings.json's env object on a switch. It implements step 4
// of the switching flow.
//
// Semantics:
//  - Token account: requires a non-empty secret (else MissingSecretException).
//    Writes the secret into ANTHROPIC_AUTH_TOKEN (AuthToken) or
//    ANTHROPIC_API_KEY (ApiKey). Writes ANTHROPIC_BASE_URL only when set.
//  - AnthropicOauth account: writes no token key; writes ANTHROPIC_BASE_URL
//    only when set. Secret is not required (restored to credential store elsewhere).
//  - If proxy.Enabled: adds HTTP_PROXY / HTTPS_PROXY (= proxy.Url) and
//    NO_PROXY (= proxy.NoProxy).
//  - ExtraEnv is always merged last (may add arbitrary keys or override the above).

namespace CCSwitcher.Core;

/// <summary>Thrown when a Token account has no secret (null or empty).
/// We never write an empty ANTHROPIC_AUTH_TOKEN / ANTHROPIC_API_KEY; the caller
/// must abort before any writes.</summary>
public sealed class MissingSecretException : Exception
{
    public MissingSecretException() : base("token account is missing its secret") { }
}

/// <summary>Builds the env dictionary ccswitcher will write for the target account.</summary>
public static class EnvBuilder
{
    /// <summary>
    /// Build the env dictionary for the target account.
    /// </summary>
    /// <param name="account">Target account.</param>
    /// <param name="secret">Token value from keyring; null for OAuth accounts.</param>
    /// <param name="proxy">Global proxy settings.</param>
    /// <returns>The env key/value pairs to inject into settings.json.</returns>
    /// <exception cref="MissingSecretException">
    /// Thrown when account is Token and secret is null or empty.
    /// </exception>
    public static Dictionary<string, string> Build(
        Account account,
        string? secret,
        ProxySettings proxy)
    {
        var env = new Dictionary<string, string>();

        switch (account.AccountType)
        {
            case AccountType.Token:
            {
                // Treat empty string the same as null — never write a blank token.
                var effectiveSecret = string.IsNullOrEmpty(secret) ? null : secret;
                if (effectiveSecret is null)
                    throw new MissingSecretException();

                // Default to AuthToken when auth_kind is unset.
                var key = (account.AuthKind ?? Core.AuthKind.AuthToken) switch
                {
                    Core.AuthKind.AuthToken => "ANTHROPIC_AUTH_TOKEN",
                    Core.AuthKind.ApiKey    => "ANTHROPIC_API_KEY",
                    _                       => throw new InvalidOperationException("Unknown AuthKind"),
                };
                env[key] = effectiveSecret;
                break;
            }
            case AccountType.AnthropicOauth:
                // No token key written for OAuth accounts; secret (if any) is
                // restored to the credential store elsewhere.
                break;
        }

        // base_url applies to BOTH account types, only when the account carries one.
        if (!string.IsNullOrEmpty(account.BaseUrl))
            env["ANTHROPIC_BASE_URL"] = account.BaseUrl;

        if (proxy.Enabled)
        {
            env["HTTP_PROXY"]  = proxy.Url;
            env["HTTPS_PROXY"] = proxy.Url;
            env["NO_PROXY"]    = proxy.NoProxy;
        }

        // ExtraEnv merged last; may add arbitrary keys (or override the above).
        foreach (var (k, v) in account.ExtraEnv)
            env[k] = v;

        return env;
    }
}
