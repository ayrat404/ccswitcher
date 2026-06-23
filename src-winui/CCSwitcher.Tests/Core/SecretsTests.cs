// Tests for Secrets.Sanitize — mirrors the sanitize_secrets tests in commands.rs.

using CCSwitcher.Core;
using Xunit;

namespace CCSwitcher.Tests.Core;

public sealed class SecretsTests
{
    // -----------------------------------------------------------------------
    // Token patterns
    // -----------------------------------------------------------------------

    [Fact]
    public void Sanitize_RedactsSkAntToken()
    {
        var result = Secrets.Sanitize("token sk-ant-12345AbcDef");
        Assert.Contains("sk-ant-***", result);
        Assert.DoesNotContain("sk-ant-12345AbcDef", result);
    }

    [Fact]
    public void Sanitize_RedactsSkToken_NotSkAnt()
    {
        var result = Secrets.Sanitize("key sk-test123_abc-XYZ");
        Assert.Contains("sk-***", result);
        Assert.DoesNotContain("sk-test123_abc-XYZ", result);
    }

    [Fact]
    public void Sanitize_RedactsSkAntBeforeSkSoNoPrefixLeak()
    {
        // sk-ant-* must be replaced before sk-* to avoid leaving "sk-***-…".
        var result = Secrets.Sanitize("sk-ant-supersecret");
        Assert.Equal("sk-ant-***", result);
        // Must not contain "sk-***-***" or similar double-replacement artifact.
        Assert.DoesNotContain("sk-***-***", result);
    }

    // -----------------------------------------------------------------------
    // Bearer tokens
    // -----------------------------------------------------------------------

    [Fact]
    public void Sanitize_RedactsBearerToken()
    {
        var result = Secrets.Sanitize("Authorization: Bearer abc123def456");
        Assert.Contains("Bearer ***", result);
        Assert.DoesNotContain("abc123def456", result);
    }

    [Fact]
    public void Sanitize_RedactsBearerTokenWithDots()
    {
        var result = Secrets.Sanitize("Bearer eyJhbGciOiJSUzI1NiJ9.payload.sig");
        Assert.Contains("Bearer ***", result);
        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiJ9", result);
    }

    // -----------------------------------------------------------------------
    // OAuth JSON credential fields
    // -----------------------------------------------------------------------

    [Fact]
    public void Sanitize_RedactsAccessTokenField()
    {
        var input = """{"accessToken":"supersecret","other":"value"}""";
        var result = Secrets.Sanitize(input);
        Assert.Contains(@"""accessToken"": ""***""", result);
        Assert.DoesNotContain("supersecret", result);
    }

    [Fact]
    public void Sanitize_RedactsRefreshTokenField()
    {
        var input = """{"refreshToken":"refresh-secret"}""";
        var result = Secrets.Sanitize(input);
        Assert.Contains(@"""refreshToken"": ""***""", result);
        Assert.DoesNotContain("refresh-secret", result);
    }

    [Fact]
    public void Sanitize_RedactsIdTokenField()
    {
        var input = """{"idToken":"id-token-value"}""";
        var result = Secrets.Sanitize(input);
        Assert.Contains(@"""idToken"": ""***""", result);
        Assert.DoesNotContain("id-token-value", result);
    }

    [Fact]
    public void Sanitize_RedactsMultipleFieldsInBlob()
    {
        var input = """{"accessToken":"at","refreshToken":"rt","idToken":"it"}""";
        var result = Secrets.Sanitize(input);
        Assert.Contains(@"""accessToken"": ""***""", result);
        Assert.Contains(@"""refreshToken"": ""***""", result);
        Assert.Contains(@"""idToken"": ""***""", result);
        Assert.DoesNotContain("\"at\"", result);
        Assert.DoesNotContain("\"rt\"", result);
        Assert.DoesNotContain("\"it\"", result);
    }

    // -----------------------------------------------------------------------
    // Non-secret input
    // -----------------------------------------------------------------------

    [Fact]
    public void Sanitize_PlainTextWithoutSecretsIsUnchanged()
    {
        const string input = "account not found: my-account-id";
        Assert.Equal(input, Secrets.Sanitize(input));
    }

    [Fact]
    public void Sanitize_EmptyStringReturnsEmpty()
    {
        Assert.Equal(string.Empty, Secrets.Sanitize(string.Empty));
    }
}
