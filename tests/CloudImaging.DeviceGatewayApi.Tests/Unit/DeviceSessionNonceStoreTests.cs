using CloudImaging.DeviceGatewayApi.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="DeviceSessionNonceStore"/> (FR-069).
/// Uses an in-memory registration delegate to prove single-use / replay-rejection semantics
/// without a real Table Storage backend.
/// </summary>
public sealed class DeviceSessionNonceStoreTests
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    private DeviceSessionNonceStore BuildStore()
    {
        Task<bool> Register(string nonceKey, DateTimeOffset expiresAt, CancellationToken ct)
            => Task.FromResult(_seen.Add(nonceKey)); // HashSet.Add returns false if already present

        return new DeviceSessionNonceStore(Register, NullLogger<DeviceSessionNonceStore>.Instance);
    }

    [Fact]
    public async Task TryConsumeAsync_FirstUse_ReturnsTrue()
    {
        var store = BuildStore();

        var result = await store.TryConsumeAsync("nonce-A", DateTimeOffset.UtcNow.AddMinutes(5));

        result.Should().BeTrue("a first-seen nonce must be accepted");
    }

    [Fact]
    public async Task TryConsumeAsync_SameNonceTwice_SecondReturnsFalse()
    {
        var store = BuildStore();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);

        var first  = await store.TryConsumeAsync("nonce-A", expiry);
        var second = await store.TryConsumeAsync("nonce-A", expiry);

        first.Should().BeTrue();
        second.Should().BeFalse("replaying the same nonce must be rejected");
    }

    [Fact]
    public async Task TryConsumeAsync_DifferentNonces_BothReturnTrue()
    {
        var store = BuildStore();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);

        var a = await store.TryConsumeAsync("nonce-A", expiry);
        var b = await store.TryConsumeAsync("nonce-B", expiry);

        a.Should().BeTrue();
        b.Should().BeTrue("distinct nonces are independent");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TryConsumeAsync_NullOrWhitespaceNonce_Throws(string? nonce)
    {
        var store = BuildStore();

        var act = async () => await store.TryConsumeAsync(nonce!, DateTimeOffset.UtcNow.AddMinutes(5));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void HashNonce_IsDeterministicHexAndTableKeySafe()
    {
        // Base64 nonces can contain '/', '+', '=' which are unsafe in a Table RowKey; the hash must not.
        const string rawNonce = "ab/cd+ef==";

        var hash1 = DeviceSessionNonceStore.HashNonce(rawNonce);
        var hash2 = DeviceSessionNonceStore.HashNonce(rawNonce);

        hash1.Should().Be(hash2, "hashing must be deterministic");
        hash1.Should().HaveLength(64, "SHA-256 hex is 64 characters");
        hash1.Should().MatchRegex("^[0-9A-F]+$", "hex digest must be Table-key safe");
    }

    [Fact]
    public void HashNonce_DifferentNonces_ProduceDifferentKeys()
    {
        DeviceSessionNonceStore.HashNonce("nonce-A")
            .Should().NotBe(DeviceSessionNonceStore.HashNonce("nonce-B"));
    }
}
