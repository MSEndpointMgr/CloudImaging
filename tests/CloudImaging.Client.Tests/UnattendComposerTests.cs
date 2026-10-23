using System.Text;
using System.Xml;
using System.Xml.Linq;
using CloudImaging.Client.Services.Setup;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// The answer file written to Windows\Panther: the profile's own file or an empty one, and with
/// Windows setup work exactly one setup runner command, first in specialize, for this device's
/// architecture. Everything else in the profile's file is kept.
/// </summary>
public sealed class UnattendComposerTests
{
    private static readonly XNamespace U = UnattendComposer.Unattend;

    private static XDocument Compose(string? answerFile, bool work, MachineArchitecture architecture = MachineArchitecture.X64)
    {
        var bytes = UnattendComposer.Compose(answerFile, work, architecture);
        return XDocument.Parse(Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void WithoutWork_AndWithoutAnAnswerFile_WritesAnEmptyUnattend()
    {
        var bytes = UnattendComposer.Compose(null, hasWindowsSetupWork: false, MachineArchitecture.X64);
        var text = Encoding.UTF8.GetString(bytes);

        text.Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "the declaration names the encoding; no BOM is needed");
        var doc = XDocument.Parse(text);
        doc.Root!.Name.Should().Be(U + "unattend");
        doc.Root.HasElements.Should().BeFalse();
    }

    [Fact]
    public void WithoutWork_TheProfileAnswerFileIsKept()
    {
        const string profile = """<unattend xmlns="urn:schemas-microsoft-com:unattend"><settings pass="oobeSystem"><component name="Microsoft-Windows-International-Core" processorArchitecture="amd64"><UILanguage>sv-SE</UILanguage></component></settings></unattend>""";

        var doc = Compose(profile, work: false);

        doc.Descendants(U + "UILanguage").Single().Value.Should().Be("sv-SE");
        UnattendComposer.CountRunnerCommands(doc).Should().Be(0);
    }

    [Fact]
    public void Doctype_IsRefused()
    {
        const string evil = """<?xml version="1.0"?><!DOCTYPE unattend [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><unattend xmlns="urn:schemas-microsoft-com:unattend">&x;</unattend>""";

        var act = () => UnattendComposer.Compose(evil, hasWindowsSetupWork: false, MachineArchitecture.X64);

        act.Should().Throw<XmlException>();
    }

    [Fact]
    public void WithWork_AddsTheRunnerFirst_InTheDeviceArchitectureComponent()
    {
        var doc = Compose(null, work: true, MachineArchitecture.Arm64);

        UnattendComposer.CountRunnerCommands(doc).Should().Be(1);
        var component = doc.Descendants(U + "component").Single();
        component.Attribute("processorArchitecture")!.Value.Should().Be("arm64");
        component.Parent!.Attribute("pass")!.Value.Should().Be("specialize");
        var command = component.Descendants(U + "RunSynchronousCommand").Single();
        command.Element(U + "Order")!.Value.Should().Be("1");
        command.Element(U + "Path")!.Value.Should().Be(UnattendComposer.RunnerCommand);
    }

    [Fact]
    public void WithWork_ExistingCommandsMoveUpAndKeepTheirOrder()
    {
        const string profile = """
            <unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
              <settings pass="specialize">
                <component name="Microsoft-Windows-Deployment" processorArchitecture="amd64">
                  <RunSynchronous>
                    <RunSynchronousCommand wcm:action="add"><Order>5</Order><Path>b.cmd</Path></RunSynchronousCommand>
                    <RunSynchronousCommand wcm:action="add"><Path>c.cmd</Path></RunSynchronousCommand>
                    <RunSynchronousCommand wcm:action="add"><Order>1</Order><Path>a.cmd</Path></RunSynchronousCommand>
                  </RunSynchronous>
                </component>
                <component name="Microsoft-Windows-Deployment" processorArchitecture="arm64">
                  <RunSynchronous><RunSynchronousCommand wcm:action="add"><Order>1</Order><Path>arm.cmd</Path></RunSynchronousCommand></RunSynchronous>
                </component>
              </settings>
            </unattend>
            """;

        var doc = Compose(profile, work: true);

        var amd64 = doc.Descendants(U + "component").Single(c => (string?)c.Attribute("processorArchitecture") == "amd64");
        var commands = amd64.Descendants(U + "RunSynchronousCommand").Select(c => (c.Element(U + "Order")!.Value, (string?)c.Element(U + "Path"))).ToList();
        commands.Should().Equal(("1", UnattendComposer.RunnerCommand), ("2", "a.cmd"), ("3", "b.cmd"), ("4", "c.cmd"));
        var arm64 = doc.Descendants(U + "component").Single(c => (string?)c.Attribute("processorArchitecture") == "arm64");
        arm64.Descendants(U + "Path").Single().Value.Should().Be("arm.cmd");
        UnattendComposer.CountRunnerCommands(doc).Should().Be(1);
    }

    [Fact]
    public void ANewComponent_CarriesTheAttributesAndNamespacesWindowsSetupExpects()
    {
        var doc = Compose(null, work: true);

        var component = doc.Descendants(U + "component").Single();
        component.Attribute("name")!.Value.Should().Be("Microsoft-Windows-Deployment");
        component.Attribute("processorArchitecture")!.Value.Should().Be("amd64");
        component.Attribute("publicKeyToken")!.Value.Should().Be("31bf3856ad364e35");
        component.Attribute("language")!.Value.Should().Be("neutral");
        component.Attribute("versionScope")!.Value.Should().Be("nonSxS");
        component.Attribute(XNamespace.Xmlns + "wcm")!.Value.Should().Be("http://schemas.microsoft.com/WMIConfig/2002/State");
        component.Attribute(XNamespace.Xmlns + "xsi")!.Value.Should().Be("http://www.w3.org/2001/XMLSchema-instance");
        var command = component.Descendants(U + "RunSynchronousCommand").Single();
        command.Attribute(XName.Get("action", "http://schemas.microsoft.com/WMIConfig/2002/State"))!.Value.Should().Be("add");
        command.Element(U + "Description")!.Value.Should().Be("Cloud Imaging setup runner");
        command.Element(U + "Path")!.Value.Length.Should().BeLessThan(259);
    }

    [Fact]
    public void AnExistingSpecializePassAndComponent_AreReused_TheFirstOneOfEach()
    {
        const string profile = """
            <unattend xmlns="urn:schemas-microsoft-com:unattend">
              <settings pass="specialize">
                <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64"><TimeZone>W. Europe Standard Time</TimeZone></component>
                <component name="Microsoft-Windows-Deployment" processorArchitecture="amd64"><RunSynchronous/></component>
                <component name="Microsoft-Windows-Deployment" processorArchitecture="amd64"><RunSynchronous/></component>
              </settings>
              <settings pass="specialize"/>
            </unattend>
            """;

        var doc = Compose(profile, work: true);

        doc.Root!.Elements(U + "settings").Should().HaveCount(2);
        var deployments = doc.Descendants(U + "component").Where(c => (string?)c.Attribute("name") == "Microsoft-Windows-Deployment").ToList();
        deployments.Should().HaveCount(2);
        deployments[0].Descendants(U + "RunSynchronousCommand").Should().ContainSingle();
        deployments[1].Descendants(U + "RunSynchronousCommand").Should().BeEmpty();
        doc.Descendants(U + "TimeZone").Single().Value.Should().Be("W. Europe Standard Time");
    }

    [Fact]
    public void EveryOtherElement_KeepsItsAttributesAndText()
    {
        const string profile = """
            <unattend xmlns="urn:schemas-microsoft-com:unattend">
              <settings pass="oobeSystem">
                <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <InputLocale>sv-SE</InputLocale>
                </component>
              </settings>
            </unattend>
            """;

        var withoutWork = Compose(profile, work: false);
        var withWork = Compose(profile, work: true);

        var before = withoutWork.Descendants(U + "component").Single();
        var after = withWork.Descendants(U + "component").Single(c => (string?)c.Attribute("name") == "Microsoft-Windows-International-Core");
        XNode.DeepEquals(before, after).Should().BeTrue();
    }

    [Fact]
    public void WithoutWork_NoRunnerCommandExists_EvenWhenTheProfileHasOtherCommands()
    {
        const string profile = """<unattend xmlns="urn:schemas-microsoft-com:unattend"><settings pass="specialize"><component name="Microsoft-Windows-Deployment" processorArchitecture="amd64"><RunSynchronous><RunSynchronousCommand><Order>1</Order><Path>a.cmd</Path></RunSynchronousCommand></RunSynchronous></component></settings></unattend>""";

        var doc = Compose(profile, work: false);

        UnattendComposer.CountRunnerCommands(doc).Should().Be(0);
        doc.Descendants(U + "Order").Single().Value.Should().Be("1");
    }
}
