using OPCClient.Security;

namespace OPCClient.Tests;

public class ApiKeyHasherTests
{
    [Fact]
    public void Hash_matches_known_sha256_vector() =>
        // SHA-256("abc") laut FIPS 180-2
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ApiKeyHasher.Hash("abc"));

    [Fact]
    public void TryParseHash_accepts_valid_hash() =>
        Assert.True(ApiKeyHasher.TryParseHash(ApiKeyHasher.Hash("x"), out var bytes) && bytes.Length == 32);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz16bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015adzz")]
    public void TryParseHash_rejects_invalid_input(string? hex) =>
        Assert.False(ApiKeyHasher.TryParseHash(hex, out _));
}
