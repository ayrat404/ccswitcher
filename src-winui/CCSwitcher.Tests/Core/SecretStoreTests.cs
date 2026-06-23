// Tests for SecretStore — mirrors the Rust test suite in secret_store.rs.
//
// Only InMemorySecretStore is tested here. PasswordVaultSecretStore requires
// a real Windows vault and is never exercised in automated tests.

using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public sealed class SecretStoreTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static InMemorySecretStore NewStore() => new();

    // -----------------------------------------------------------------------
    // Set + Get round-trip
    // -----------------------------------------------------------------------

    [Fact]
    public void SetThenGet_ReturnsStoredValue()
    {
        var store = NewStore();
        store.Set("acc-1", "s3cr3t");
        Assert.Equal("s3cr3t", store.Get("acc-1"));
    }

    // -----------------------------------------------------------------------
    // Get missing key
    // -----------------------------------------------------------------------

    [Fact]
    public void Get_MissingKey_ReturnsNull()
    {
        var store = NewStore();
        Assert.Null(store.Get("nope"));
    }

    // -----------------------------------------------------------------------
    // Delete removes the key
    // -----------------------------------------------------------------------

    [Fact]
    public void Delete_RemovesKey()
    {
        var store = NewStore();
        store.Set("acc-1", "s3cr3t");
        store.Delete("acc-1");
        Assert.Null(store.Get("acc-1"));
    }

    // -----------------------------------------------------------------------
    // Set overwrites existing value
    // -----------------------------------------------------------------------

    [Fact]
    public void Set_OverwritesExistingValue()
    {
        var store = NewStore();
        store.Set("acc-1", "first");
        store.Set("acc-1", "second");
        Assert.Equal("second", store.Get("acc-1"));
    }

    // -----------------------------------------------------------------------
    // Delete non-existing key is a no-op
    // -----------------------------------------------------------------------

    [Fact]
    public void Delete_NonExistingKey_IsNoOp()
    {
        var store = NewStore();
        // Must not throw.
        store.Delete("does-not-exist");
    }

    // -----------------------------------------------------------------------
    // Get after Delete returns null
    // -----------------------------------------------------------------------

    [Fact]
    public void GetAfterDelete_ReturnsNull()
    {
        var store = NewStore();
        store.Set("acc-1", "value");
        store.Delete("acc-1");
        Assert.Null(store.Get("acc-1"));
    }

    // -----------------------------------------------------------------------
    // Multiple independent keys
    // -----------------------------------------------------------------------

    [Fact]
    public void MultipleKeys_AreIndependent()
    {
        var store = NewStore();
        store.Set("acc-1", "alpha");
        store.Set("acc-2", "beta");

        Assert.Equal("alpha", store.Get("acc-1"));
        Assert.Equal("beta", store.Get("acc-2"));

        store.Delete("acc-1");
        Assert.Null(store.Get("acc-1"));
        Assert.Equal("beta", store.Get("acc-2"));
    }
}
