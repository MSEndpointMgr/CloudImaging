using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloudImaging.Client.Services;
using CloudImaging.Client.Services.Drivers;
using CloudImaging.Client.ViewModels;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests.Drivers;

public sealed class PciInventoryTests
{
    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_43F0&SUBSYS_00748086&REV_00", "8086", "43F0", "8086", "0074")]
    [InlineData(@"PCI\VEN_10ec&DEV_8168", "10EC", "8168", null, null)]
    public void HardwareIds_ParseVendorDeviceAndSubsystem(string id, string vendor, string device, string? subVendor, string? subDevice)
    {
        PciInventory.Parse(id).Should().Be(new PciHardwareId(vendor, device, subVendor, subDevice));
    }

    [Theory]
    [InlineData(@"USB\VID_0BDA&PID_4C54")]
    [InlineData(@"PCI\VEN_80&DEV_43F0")]
    [InlineData(null)]
    public void OtherIds_AreNotPciHardware(string? id)
    {
        PciInventory.Parse(id).Should().BeNull();
    }

    [Fact]
    public void AComponentMatches_OnVendorAndDevice_AndOnTheSubsystemWhenItListsOne()
    {
        var devices = new[] { new PciHardwareId("8086", "43F0", "8086", "0074") };

        PciInventory.Matches([new DriverPciId { Vendor = "8086", Device = "43F0" }], devices).Should().BeTrue();
        PciInventory.Matches([new DriverPciId { Vendor = "8086", Device = "43F0", SubVendor = "8086", SubDevice = "0074" }], devices).Should().BeTrue();
        PciInventory.Matches([new DriverPciId { Vendor = "8086", Device = "43F0", SubVendor = "8086", SubDevice = "02A0" }], devices).Should().BeFalse();
        PciInventory.Matches([new DriverPciId { Vendor = "8086", Device = "2723" }], devices).Should().BeFalse();
    }

    [Theory]
    [InlineData("  0CB4 ", "0CB4")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SystemSku_IsTrimmed_AndBlankIsNone(string? value, string? expected)
    {
        OperationSelectionViewModel.SystemSku(value).Should().Be(expected);
        OperationSelectionViewModel.SystemSku(new string('A', 80))!.Length.Should().Be(64);
    }

    [Fact]
    public async Task TheSignedRegistration_CarriesTheSystemSku()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=Boot Media Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        string? body = null;
        var handler = new LambdaHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"sessionId":"6f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10","state":"PendingCouple"}""", System.Text.Encoding.UTF8, "application/json") };
        });
        var client = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") }, certificate);

        await client.CreateSessionAsync(new DeviceRegistrationPayload
        {
            SerialNumber = "SN-1",
            Manufacturer = "Dell Inc.",
            Model = "Latitude 5450",
            Hardware = new DeviceHardwareMetadata { MotherboardModel = "0B1F", SystemSku = "0CB4" },
        });

        body.Should().Contain("\"systemSku\":\"0CB4\"").And.Contain("proofOfPossession");
    }

    private sealed class LambdaHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
