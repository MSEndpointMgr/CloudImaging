using System.IO;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Win32;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// The build-time check that a Windows RE image can run Wi-Fi, so a missing service or DLL fails
/// the build instead of a test boot on hardware.
/// </summary>
public sealed class WindowsRecoveryWifiCheckTests
{
    private const string NetworkRestricted = @"%SystemRoot%\system32\svchost.exe -k LocalSystemNetworkRestricted -p -s Wlansvc";

    /// <summary>The wlansvc tree as Windows 11 registers it: wlansvc, wcmsvc, WinHttpAutoProxySvc, Dhcp, Dnscache, NSI, RPC and the two NDIS drivers.</summary>
    private static FakeImage CompleteImage()
    {
        var image = new FakeImage();
        image.Service("Wlansvc", NetworkRestricted, @"%SystemRoot%\System32\wlansvc.dll", "nativewifip", "RpcSs", "Ndisuio", "wcmsvc");
        image.Service("wcmsvc", @"%SystemRoot%\system32\svchost.exe -k LocalServiceNetworkRestricted -p", @"%SystemRoot%\System32\wcmsvc.dll", "RpcSs", "NSI", "WinHttpAutoProxySvc");
        image.Service("WinHttpAutoProxySvc", @"%SystemRoot%\system32\svchost.exe -k LocalServiceHttp -p", @"%SystemRoot%\system32\winhttp.dll", "Dhcp", "DNSCache");
        image.Service("Dhcp", @"%SystemRoot%\system32\svchost.exe -k LocalServiceNetworkRestricted -p", @"%SystemRoot%\system32\dhcpcore.dll", "NSI", "Afd");
        image.Service("Dnscache", @"%SystemRoot%\system32\svchost.exe -k NetworkService -p", @"%SystemRoot%\System32\dnsrslvr.dll", "nsi", "Afd");
        image.Service("nsi", @"%SystemRoot%\system32\svchost.exe -k LocalService -p", @"%SystemRoot%\system32\nsisvc.dll", "rpcss", "nsiproxy");
        image.Service("RpcSs", @"%SystemRoot%\system32\svchost.exe -k rpcss -p", @"%SystemRoot%\system32\rpcss.dll", "RpcEptMapper", "DcomLaunch");
        image.Service("RpcEptMapper", @"%SystemRoot%\system32\svchost.exe -k RPCSS -p", @"%SystemRoot%\System32\RpcEpMap.dll");
        image.Service("DcomLaunch", @"%SystemRoot%\system32\svchost.exe -k DcomLaunch -p", @"%SystemRoot%\system32\rpcss.dll");
        image.Driver("nativewifip", @"system32\DRIVERS\nwifi.sys");
        image.Driver("Ndisuio", @"system32\drivers\ndisuio.sys");
        image.Driver("nsiproxy", @"system32\drivers\nsiproxy.sys");
        image.Driver("Afd", @"\SystemRoot\system32\drivers\afd.sys");
        image.Group("LocalSystemNetworkRestricted", "wlansvc");
        image.Group("LocalServiceNetworkRestricted", "wcmsvc", "DHCP");
        image.Group("LocalServiceHttp", "WinHttpAutoProxySvc");
        image.Group("NetworkService", "DNSCache");
        image.Group("LocalService", "nsi");
        image.Group("rpcss", "RpcEptMapper", "RpcSs");
        image.Group("DcomLaunch", "DcomLaunch");
        image.Netsh.Add("wlancfg.dll");
        image.Files.Add(@"System32\wlancfg.dll");
        image.ImportsOf[@"System32\wlancfg.dll"] = ["mdmregistration.dll", "api-ms-win-core-heap-l1-1-0.dll"];
        image.Files.Add(@"System32\mdmregistration.dll");
        image.ImportsOf[@"System32\mdmregistration.dll"] = ["dmcmnutils.dll"];
        image.Files.Add(@"System32\dmcmnutils.dll");
        return image;
    }

    [Fact]
    public void ACompleteImage_PassesTheCheck()
    {
        WindowsRecoveryWifiCheck.FindProblems(CompleteImage()).Should().BeEmpty();
    }

    [Fact]
    public void AMissingDependency_FarDownTheTree_IsNamedWithWhatNeedsIt()
    {
        var image = CompleteImage();
        image.Services.Remove("WinHttpAutoProxySvc");

        WindowsRecoveryWifiCheck.FindProblems(image).Should().ContainSingle()
            .Which.Should().Be("Service WinHttpAutoProxySvc (needed by wcmsvc) is not registered in the image.");
    }

    [Fact]
    public void AMissingDllImportedByTheNetshHelper_IsFound()
    {
        var image = CompleteImage();
        image.Files.Remove(@"System32\dmcmnutils.dll");

        WindowsRecoveryWifiCheck.FindProblems(image).Should().ContainSingle()
            .Which.Should().Be("dmcmnutils.dll (loaded by mdmregistration.dll) is missing from the image.");
    }

    [Fact]
    public void ADisabledServiceAMissingDriverAndAnUnlistedHost_AreEachReported()
    {
        var image = CompleteImage();
        image.Services["Dhcp"] = image.Services["Dhcp"] with { Start = 4 };
        image.Files.Remove(@"system32\DRIVERS\nwifi.sys");
        image.Groups["LocalSystemNetworkRestricted"] = [];
        image.Netsh.Clear();

        var problems = WindowsRecoveryWifiCheck.FindProblems(image);
        problems.Should().BeEquivalentTo(ExpectedProblems, string.Join(" | ", problems));
    }

    private static readonly string[] ExpectedProblems =
    [
        "Service Dhcp (needed by WinHttpAutoProxySvc) is disabled in the image.",
        "nwifi.sys is missing from the image.",
        "Service Wlansvc is not listed in the svchost group LocalSystemNetworkRestricted.",
        "The netsh wlan helper (wlancfg.dll) is not registered in the image.",
    ];

    [Theory]
    [InlineData(@"%SystemRoot%\System32\wlansvc.dll", @"System32\wlansvc.dll")]
    [InlineData(@"\SystemRoot\system32\drivers\afd.sys", @"system32\drivers\afd.sys")]
    [InlineData(@"system32\DRIVERS\nwifi.sys", @"system32\DRIVERS\nwifi.sys")]
    [InlineData(@"C:\Windows\system32\lsass.exe", @"system32\lsass.exe")]
    [InlineData("winhttp.dll", @"System32\winhttp.dll")]
    public void RegistryPaths_AreMadeRelativeToTheWindowsFolder(string registryPath, string expected)
    {
        WindowsRecoveryWifiCheck.ToImageRelative(registryPath).Should().Be(expected);
    }

    [Fact]
    public void ImportsAreReadFromARealBinary()
    {
        var imports = WindowsRecoveryWifiCheck.ReadImports(Path.Combine(Environment.SystemDirectory, "netsh.exe"));

        imports.Should().NotBeEmpty();
        imports.Should().OnlyContain(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The rules, run against the Windows this test runs on. A full Windows with Wi-Fi must pass, so a
    /// rule that would wrongly fail a good Windows RE image fails here first. Skipped where Wi-Fi is
    /// not installed (Windows Server build agents).
    /// </summary>
    [Fact]
    public void TheRunningWindows_PassesTheCheck_WhenItHasWifi()
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WlanSvc");
        if (services is null)
            return;

        WindowsRecoveryWifiCheck.FindProblems(new LiveWindows()).Should().BeEmpty();
    }

    private sealed class LiveWindows : IRecoveryImageView
    {
        private static readonly string WindowsDir = Path.GetDirectoryName(Environment.SystemDirectory)!;

        public ImageService? GetService(string name) =>
            WindowsRecoveryWifiCheck.ReadService(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{name}");

        public IReadOnlyList<string> SvchostGroup(string group)
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Svchost");
            return key?.GetValue(group) as string[] ?? [];
        }

        public IReadOnlyList<string> NetshHelpers()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NetSh");
            return key is null ? [] : [.. key.GetValueNames().Select(n => key.GetValue(n) as string).OfType<string>().Select(Path.GetFileName).OfType<string>()];
        }

        public bool FileExists(string path) => File.Exists(Path.Combine(WindowsDir, path));

        public IReadOnlyList<string> ImportedDlls(string path) => WindowsRecoveryWifiCheck.ReadImports(Path.Combine(WindowsDir, path));
    }

    private sealed class FakeImage : IRecoveryImageView
    {
        public Dictionary<string, ImageService> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> ImportsOf { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Netsh { get; } = [];

        public void Service(string name, string imagePath, string serviceDll, params string[] dependsOn)
        {
            Services[name] = new ImageService(name, 3, imagePath, serviceDll, dependsOn);
            Files.Add(WindowsRecoveryWifiCheck.ToImageRelative(serviceDll)!);
        }

        public void Driver(string name, string imagePath)
        {
            Services[name] = new ImageService(name, 3, imagePath, null, []);
            Files.Add(WindowsRecoveryWifiCheck.ToImageRelative(imagePath)!);
        }

        public void Group(string name, params string[] members) => Groups[name] = members;

        public ImageService? GetService(string name) => Services.GetValueOrDefault(name);
        public IReadOnlyList<string> SvchostGroup(string group) => Groups.GetValueOrDefault(group) ?? [];
        public IReadOnlyList<string> NetshHelpers() => Netsh;
        public bool FileExists(string path) => Files.Contains(path);
        public IReadOnlyList<string> ImportedDlls(string path) => ImportsOf.GetValueOrDefault(path) ?? [];
    }
}
