using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

/// <summary>
/// Saved Wi-Fi networks: validation, order, per-location selection, location delete cleanup, and the
/// password-bearing set that Media Builder and the Device Gateway receive.
/// </summary>
public sealed class WifiNetworkCatalogTests
{
    private readonly WifiNetworkRepository _networks;
    private readonly LocationRepository _locations;
    private readonly FakePasswordStore _passwords = new();
    private readonly WifiNetworkCatalog _catalog;
    private readonly Guid _stockholm = Guid.NewGuid();
    private readonly Guid _gothenburg = Guid.NewGuid();

    public WifiNetworkCatalogTests()
    {
        var (tableService, _) = InMemoryTableClient.CreateService();
        _networks = new WifiNetworkRepository(tableService);
        _locations = new LocationRepository(tableService);
        _catalog = new WifiNetworkCatalog(_networks, _locations, _passwords, NullLogger<WifiNetworkCatalog>.Instance);
        _locations.CreateAsync(new Location { LocationId = _stockholm, Name = "Stockholm HQ", CountryCode = "SE" }).GetAwaiter().GetResult();
        _locations.CreateAsync(new Location { LocationId = _gothenburg, Name = "Gothenburg", CountryCode = "SE" }).GetAwaiter().GetResult();
    }

    private static WifiNetworkInput Input(string ssid = "Contoso-Imaging", string? password = "correct horse", bool all = true, Guid[]? locations = null) => new()
    {
        Ssid = ssid,
        Security = WifiSecurityType.Wpa3Personal,
        Password = password,
        AllLocations = all,
        LocationIds = locations ?? [],
        ChangedBy = "anna@contoso.com",
    };

    [Fact]
    public async Task Create_StoresThePasswordInTheStore_NotInTheTable()
    {
        var result = await _catalog.CreateAsync(Input());

        result.Error.Should().BeNull();
        _passwords.Values[result.Network!.NetworkId].Should().Be("correct horse");
        result.Network.Order.Should().Be(1);
        result.Network.PasswordChangedBy.Should().Be("anna@contoso.com");
        System.Text.Json.JsonSerializer.Serialize(await _catalog.ListAsync()).Should().NotContain("correct horse");
    }

    [Theory]
    [InlineData("", "correct horse", "Network name is required.")]
    [InlineData("Lab", "short", "Use 8 to 63 characters.")]
    [InlineData("Lab", null, "Password is required.")]
    public async Task Create_RejectsInvalidInput(string ssid, string? password, string expected)
    {
        var result = await _catalog.CreateAsync(Input(ssid, password));

        result.Error.Should().Be(expected);
        (await _catalog.ListAsync()).Should().BeEmpty();
        _passwords.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_RejectsADuplicateName_AndUnknownLocations()
    {
        await _catalog.CreateAsync(Input());

        (await _catalog.CreateAsync(Input())).Error.Should().Be("A network named \"Contoso-Imaging\" already exists.");
        (await _catalog.CreateAsync(Input("Lab", all: false, locations: [Guid.NewGuid()]))).Error.Should().Contain("no longer exists");
    }

    [Fact]
    public void ValidatePassword_AcceptsA64CharacterHexKey_AndRejectsNonAscii()
    {
        WifiNetwork.ValidatePassword(new string('a', 64)).Should().BeNull();
        WifiNetwork.ValidatePassword(new string('z', 64)).Should().Be("A 64-character password must be a hex key.");
        WifiNetwork.ValidatePassword("lösenord123").Should().Be("Use printable ASCII characters only.");
        WifiNetwork.ValidateSsid(new string('å', 17)).Should().Be("Network name can be at most 32 bytes.");
    }

    [Fact]
    public async Task Update_WithoutAPassword_KeepsTheStoredPasswordAndItsDate()
    {
        var created = (await _catalog.CreateAsync(Input())).Network!;

        var updated = await _catalog.UpdateAsync(created.NetworkId, Input("Contoso-Imaging-2", password: null));

        updated.Network!.Ssid.Should().Be("Contoso-Imaging-2");
        updated.Network.PasswordChangedAt.Should().Be(created.PasswordChangedAt);
        _passwords.Values[created.NetworkId].Should().Be("correct horse");
        (await _catalog.UpdateAsync(Guid.NewGuid(), Input())).NotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Order_IsSetFromTheFullList_AndDeleteClosesTheGap()
    {
        var a = (await _catalog.CreateAsync(Input("A"))).Network!;
        var b = (await _catalog.CreateAsync(Input("B"))).Network!;
        var c = (await _catalog.CreateAsync(Input("C"))).Network!;

        (await _catalog.SetOrderAsync([c.NetworkId, a.NetworkId])).Should().NotBeNull();
        (await _catalog.SetOrderAsync([c.NetworkId, a.NetworkId, b.NetworkId])).Should().BeNull();
        (await _catalog.ListAsync()).Select(n => n.Ssid).Should().Equal("C", "A", "B");

        await _catalog.DeleteAsync(a.NetworkId);

        (await _catalog.ListAsync()).Select(n => (n.Ssid, n.Order)).Should().Equal(("C", 1), ("B", 2));
        _passwords.Values.Should().NotContainKey(a.NetworkId);
    }

    [Fact]
    public async Task SetLocationNetworks_OnlyChangesSelectedLocationNetworks()
    {
        var everywhere = (await _catalog.CreateAsync(Input("Everywhere"))).Network!;
        var sto = (await _catalog.CreateAsync(Input("STO-Deploy", all: false, locations: [_stockholm]))).Network!;
        var lab = (await _catalog.CreateAsync(Input("GOT-Lab", all: false, locations: [_stockholm]))).Network!;

        var result = await _catalog.SetLocationNetworksAsync(_gothenburg, [lab.NetworkId]);

        result.Error.Should().BeNull();
        var byId = (await _catalog.ListAsync()).ToDictionary(n => n.NetworkId);
        byId[everywhere.NetworkId].AllLocations.Should().BeTrue();
        byId[sto.NetworkId].LocationIds.Should().Equal(_stockholm);
        byId[lab.NetworkId].LocationIds.Should().BeEquivalentTo([_stockholm, _gothenburg]);

        await _catalog.SetLocationNetworksAsync(_stockholm, []);
        (await _catalog.ListAsync()).Single(n => n.NetworkId == lab.NetworkId).LocationIds.Should().Equal(_gothenburg);
        (await _catalog.SetLocationNetworksAsync(Guid.NewGuid(), [])).NotFound.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveLocation_TakesTheDeletedLocationOffEveryNetwork()
    {
        await _catalog.CreateAsync(Input("STO-Deploy", all: false, locations: [_stockholm, _gothenburg]));

        var changed = await _networks.RemoveLocationAsync(_stockholm);

        changed.Should().ContainSingle();
        (await _catalog.ListAsync()).Single().LocationIds.Should().Equal(_gothenburg);
    }

    [Fact]
    public async Task ManagedSet_HasEnabledNetworksForTheLocation_InOrder_WithPasswords()
    {
        await _catalog.CreateAsync(Input("Everywhere"));
        await _catalog.CreateAsync(Input("STO-Deploy", "sto-password", all: false, locations: [_stockholm]));
        var off = (await _catalog.CreateAsync(Input("Off", all: true))).Network!;
        await _catalog.UpdateAsync(off.NetworkId, new WifiNetworkInput { Ssid = "Off", Security = WifiSecurityType.Wpa2Personal, AllLocations = true, Enabled = false });

        var stockholm = await _catalog.BuildManagedSetAsync(_stockholm, "device-gateway");
        var none = await _catalog.BuildManagedSetAsync(null, "media-builder");

        stockholm.Networks.Select(n => (n.Ssid, n.Password)).Should().Equal(("Everywhere", "correct horse"), ("STO-Deploy", "sto-password"));
        none.Networks.Select(n => n.Ssid).Should().Equal("Everywhere");
        _passwords.Reads.Should().Contain("device-gateway").And.Contain("media-builder");
    }

    [Fact]
    public void Envelope_RoundTrips_AndRejectsAnotherCertificate()
    {
        using var cert = CreateCertificate();
        using var other = CreateCertificate();
        var set = new ManagedWifiNetworkSet { LocationId = _stockholm, Networks = [new ManagedWifiNetwork("STO-Deploy", true, WifiSecurityType.Wpa2Personal, "sto-password")] };

        var bytes = ManagedWifiEnvelope.Encrypt(set, cert);
        var decrypted = ManagedWifiEnvelope.Decrypt(bytes, cert);

        System.Text.Encoding.UTF8.GetString(bytes).Should().NotContain("sto-password");
        decrypted.Networks.Should().Equal(set.Networks);
        decrypted.LocationId.Should().Be(_stockholm);
        FluentActions.Invoking(() => ManagedWifiEnvelope.Decrypt(bytes, other)).Should().Throw<ManagedWifiEnvelopeException>().WithMessage("*different boot media certificate*");
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=CloudImaging-BootMedia-Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class FakePasswordStore() : WifiPasswordStore(NullLogger<WifiPasswordStore>.Instance)
    {
        public Dictionary<Guid, string> Values { get; } = [];

        public List<string> Reads { get; } = [];

        public override Task SetAsync(Guid networkId, string password, CancellationToken ct = default)
        {
            Values[networkId] = password;
            return Task.CompletedTask;
        }

        public override Task<string?> GetAsync(Guid networkId, string purpose, CancellationToken ct = default)
        {
            Reads.Add(purpose);
            return Task.FromResult(Values.TryGetValue(networkId, out var value) ? value : null);
        }

        public override Task DeleteAsync(Guid networkId, CancellationToken ct = default)
        {
            Values.Remove(networkId);
            return Task.CompletedTask;
        }
    }
}
