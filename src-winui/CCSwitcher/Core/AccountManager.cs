// CRUD operations for managing accounts in the application config.
//
// Port of the add_token_account, update_account, and delete_account commands
// from src-tauri/src/commands.rs.
//
// AccountManager does not hold any state; every method receives the mutable
// AppConfig and all required dependencies as parameters so it is fully
// testable without a live OS keyring or filesystem.

namespace CCSwitcher.Core;

/// <summary>
/// Thrown when an operation targets an account id that does not exist in the
/// current config.
/// </summary>
public sealed class AccountNotFoundException : Exception
{
    public AccountNotFoundException(string id)
        : base($"account not found: {id}") { }
}

/// <summary>
/// Static helpers for adding, updating, and deleting accounts.
/// </summary>
public static class AccountManager
{
    /// <summary>
    /// Add a new token account.
    /// <para>
    /// Generates a fresh UUID as the account id, stores the <paramref name="secret"/>
    /// in the keyring under that id, appends the new <see cref="Account"/> to
    /// <paramref name="config"/>, and saves the config.
    /// </para>
    /// </summary>
    /// <param name="config">Current application config (mutated in place).</param>
    /// <param name="name">Display name for the new account.</param>
    /// <param name="baseUrl">Optional ANTHROPIC_BASE_URL override.</param>
    /// <param name="authKind">Whether the secret is an auth token or an API key.</param>
    /// <param name="secret">The raw token/key to store in the keyring.</param>
    /// <param name="secretStore">OS keyring (or in-memory mock for tests).</param>
    /// <param name="configDir">Directory where config.json is saved.</param>
    /// <returns>The newly created <see cref="Account"/>.</returns>
    public static Account AddTokenAccount(
        AppConfig config,
        string name,
        string? baseUrl,
        AuthKind authKind,
        string secret,
        ISecretStore secretStore,
        string configDir)
    {
        var id = Guid.NewGuid().ToString();

        // Persist the secret before adding to config so that if the keyring
        // write fails we have not modified config yet.
        secretStore.Set(id, secret);

        var account = new Account
        {
            Id          = id,
            Name        = name,
            AccountType = AccountType.Token,
            BaseUrl     = string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
            AuthKind    = authKind,
        };

        config.Accounts.Add(account);
        ConfigStore.Save(configDir, config);

        return account;
    }

    /// <summary>
    /// Update an existing account's metadata.
    /// <para>
    /// Finds the account by <paramref name="accountId"/> and updates name,
    /// base URL, and auth kind.  When <paramref name="newSecret"/> is non-null
    /// and non-empty the keyring entry is also updated; otherwise the existing
    /// keyring secret is left untouched.
    /// </para>
    /// </summary>
    /// <exception cref="AccountNotFoundException">
    /// Thrown when no account with <paramref name="accountId"/> is found.
    /// </exception>
    public static void UpdateAccount(
        AppConfig config,
        string accountId,
        string name,
        string? baseUrl,
        AuthKind? authKind,
        string? newSecret,
        ISecretStore secretStore,
        string configDir)
    {
        var index = config.Accounts.FindIndex(a => a.Id == accountId);
        if (index < 0)
            throw new AccountNotFoundException(accountId);

        var existing = config.Accounts[index];

        // Rebuild the account record with updated fields.
        var updated = new Account
        {
            Id          = existing.Id,
            Name        = name,
            AccountType = existing.AccountType,
            BaseUrl     = string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
            AuthKind    = authKind ?? existing.AuthKind,
            Identity    = existing.Identity,
            ExtraEnvNullable = existing.ExtraEnvNullable,
        };

        config.Accounts[index] = updated;

        // Update the keyring secret only when a new secret is supplied.
        if (!string.IsNullOrEmpty(newSecret))
            secretStore.Set(accountId, newSecret);

        ConfigStore.Save(configDir, config);
    }

    /// <summary>
    /// Delete an account.
    /// <para>
    /// Steps (in order):
    /// <list type="number">
    ///   <item>Remove the account from <c>config.Accounts</c>.</item>
    ///   <item>If the deleted account was the active account, clear
    ///         <c>config.ActiveAccountId</c> (prevents dangling active-id from
    ///         triggering a spurious capture-on-switch-out).</item>
    ///   <item>Delete the keyring secret for the account id.</item>
    ///   <item>Delete the <c>{id}#oauthAccount</c> keyring entry.</item>
    ///   <item>Save the updated config.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <exception cref="AccountNotFoundException">
    /// Thrown when no account with <paramref name="accountId"/> is found.
    /// </exception>
    public static void DeleteAccount(
        AppConfig config,
        string accountId,
        ISecretStore secretStore,
        string configDir)
    {
        var found = config.Accounts.RemoveAll(a => a.Id == accountId) > 0;
        if (!found)
            throw new AccountNotFoundException(accountId);

        // Clear dangling active id before persisting.
        if (config.ActiveAccountId == accountId)
            config.ActiveAccountId = null;

        // Remove both keyring entries for this account.
        secretStore.Delete(accountId);
        secretStore.Delete(UserConfig.OauthAccountKey(accountId));

        ConfigStore.Save(configDir, config);
    }
}
