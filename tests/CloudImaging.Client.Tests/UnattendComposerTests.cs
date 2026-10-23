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
}
