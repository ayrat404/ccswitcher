// Secret storage behind an interface, with a PasswordVault backend and an
// in-memory mock for tests.
//
// Port of src-tauri/src/core/secret_store.rs.
//
// Secrets (token strings, OAuth credential snapshots) never live in
// config.json; they are keyed by account id in the Windows PasswordVault.
// The ISecretStore interface keeps the core logic testable with
// InMemorySecretStore so unit tests never touch the real OS credential store.
//
// PasswordVaultSecretStore is conditionally compiled for Windows 10 19041+
// only. The test project targets plain net8.0 and compiles this file too, so
// platform-specific code is guarded by the preprocessor symbol that the
// windows10.0.19041.0 TFM defines.

namespace CCSwitcher.Core;

/// <summary>
/// Stores per-account secrets keyed by account <c>id</c>.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Store (or overwrite) the secret for <paramref name="id"/>.
    /// </summary>
    void Set(string id, string value);

    /// <summary>
    /// Fetch the secret for <paramref name="id"/>.
    /// Returns <see langword="null"/> when no entry exists for that id.
    /// </summary>
    string? Get(string id);

    /// <summary>
    /// Delete the secret for <paramref name="id"/>.
    /// Deleting a missing entry is a no-op (does not throw).
    /// </summary>
    void Delete(string id);
}

#if WINDOWS10_0_19041_0_OR_GREATER
/// <summary>
/// <see cref="ISecretStore"/> backed by the Windows
/// <see cref="Windows.Security.Credentials.PasswordVault"/>.
/// Service name is <c>"ccswitcher"</c>; resource = account id.
/// </summary>
/// <remarks>
/// This implementation can only be instantiated and used on Windows with a
/// real vault.  Tests use <see cref="InMemorySecretStore"/> instead.
/// </remarks>
public sealed class PasswordVaultSecretStore : ISecretStore
{
    private const string ServiceName = "ccswitcher";
    private readonly Windows.Security.Credentials.PasswordVault _vault = new();

    /// <inheritdoc/>
    public void Set(string id, string value)
    {
        // Remove any existing entry first so we can add the updated one.
        try
        {
            var existing = _vault.Retrieve(ServiceName, id);
            _vault.Remove(existing);
        }
        catch (Exception)
        {
            // Entry did not exist — nothing to remove.
        }

        _vault.Add(new Windows.Security.Credentials.PasswordCredential(ServiceName, id, value));
    }

    /// <inheritdoc/>
    public string? Get(string id)
    {
        try
        {
            var credential = _vault.Retrieve(ServiceName, id);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception)
        {
            // Entry not found.
            return null;
        }
    }

    /// <inheritdoc/>
    public void Delete(string id)
    {
        try
        {
            var credential = _vault.Retrieve(ServiceName, id);
            _vault.Remove(credential);
        }
        catch (Exception)
        {
            // Entry did not exist — no-op.
        }
    }
}
#endif // WINDOWS10_0_19041_0_OR_GREATER

/// <summary>
/// In-memory <see cref="ISecretStore"/> for tests.
/// Backed by a plain <see cref="Dictionary{TKey,TValue}"/>.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _store = new();

    /// <inheritdoc/>
    public void Set(string id, string value) => _store[id] = value;

    /// <inheritdoc/>
    public string? Get(string id) => _store.TryGetValue(id, out var v) ? v : null;

    /// <inheritdoc/>
    public void Delete(string id) => _store.Remove(id);
}
