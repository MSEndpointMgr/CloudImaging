using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CloudImaging.Contracts.Models;

namespace CloudImaging.Client.Services.Setup;

/// <summary>
/// Builds the answer file written to <c>Windows\Panther\unattend.xml</c>: the profile's own answer
/// file, or an empty one, plus (when there is Windows setup work) one RunSynchronous command in
/// specialize that starts the Cloud Imaging setup runner first. Pure, so every merge case is
/// unit-tested.
/// </summary>
public static class UnattendComposer
{
    /// <summary>The unattend namespace every element of an answer file is in.</summary>
    public static readonly XNamespace Unattend = "urn:schemas-microsoft-com:unattend";

    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>File name the runner command starts; the post-condition looks for it.</summary>
    public const string RunnerScriptName = "Invoke-CloudImagingSetup.ps1";

    /// <summary>The command that starts the setup runner.</summary>
    public const string RunnerCommand = "%WINDIR%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%WINDIR%\\Setup\\CloudImaging\\" + RunnerScriptName + "\"";

    private const string EmptyAnswerFile = "<?xml version=\"1.0\" encoding=\"utf-8\"?><unattend xmlns=\"urn:schemas-microsoft-com:unattend\"/>";

    /// <summary>Returns the answer file as UTF-8 bytes with an XML declaration, indented.</summary>
    public static byte[] Compose(string? profileAnswerFile, bool hasWindowsSetupWork, MachineArchitecture architecture)
    {
        var document = Parse(string.IsNullOrWhiteSpace(profileAnswerFile) ? EmptyAnswerFile : profileAnswerFile);
        if (hasWindowsSetupWork)
        {
            AddRunner(document, architecture);
        }
        return Serialize(document);
    }

    /// <summary>Parses without DTDs or external resolution, so an answer file cannot pull anything in.</summary>
    public static XDocument Parse(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    /// <summary>Number of runner commands in the document, wherever they are.</summary>
    public static int CountRunnerCommands(XDocument document) =>
        document.Descendants(Unattend + "RunSynchronousCommand")
            .Count(c => ((string?)c.Element(Unattend + "Path"))?.TrimEnd().EndsWith(RunnerScriptName + "\"", StringComparison.OrdinalIgnoreCase) == true);

    private static void AddRunner(XDocument document, MachineArchitecture architecture)
    {
        var root = document.Root ?? throw new InvalidOperationException("The answer file has no root element.");
        var processor = MachineArchitecturePlatform.AdkArchitectureName(architecture);

        var specialize = root.Elements(Unattend + "settings").FirstOrDefault(s => (string?)s.Attribute("pass") == "specialize");
        if (specialize is null)
        {
            specialize = new XElement(Unattend + "settings", new XAttribute("pass", "specialize"));
            root.Add(specialize);
        }

        var component = specialize.Elements(Unattend + "component").FirstOrDefault(c =>
            (string?)c.Attribute("name") == "Microsoft-Windows-Deployment"
            && string.Equals((string?)c.Attribute("processorArchitecture"), processor, StringComparison.OrdinalIgnoreCase));
        if (component is null)
        {
            component = new XElement(Unattend + "component",
                new XAttribute("name", "Microsoft-Windows-Deployment"),
                new XAttribute("processorArchitecture", processor),
                new XAttribute("publicKeyToken", "31bf3856ad364e35"),
                new XAttribute("language", "neutral"),
                new XAttribute("versionScope", "nonSxS"),
                new XAttribute(XNamespace.Xmlns + "wcm", Wcm.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName));
            specialize.Add(component);
        }

        var runSynchronous = component.Element(Unattend + "RunSynchronous");
        if (runSynchronous is null)
        {
            runSynchronous = new XElement(Unattend + "RunSynchronous");
            component.Add(runSynchronous);
        }

        // Existing commands keep their relative order (missing or invalid Order last) and move up
        // one, so the runner is always first.
        var existing = runSynchronous.Elements(Unattend + "RunSynchronousCommand")
            .Select((command, position) => (command, position, order: int.TryParse((string?)command.Element(Unattend + "Order"), out var o) ? o : int.MaxValue))
            .OrderBy(e => e.order)
            .ThenBy(e => e.position)
            .Select(e => e.command)
            .ToList();
        foreach (var command in existing)
        {
            command.Remove();
        }

        var next = 2;
        foreach (var command in existing)
        {
            var order = command.Element(Unattend + "Order");
            if (order is null)
            {
                command.AddFirst(new XElement(Unattend + "Order", next));
            }
            else
            {
                order.Value = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            next++;
        }

        runSynchronous.AddFirst(
            new XElement(Unattend + "RunSynchronousCommand",
                new XAttribute(Wcm + "action", "add"),
                new XElement(Unattend + "Order", 1),
                new XElement(Unattend + "Description", "Cloud Imaging setup runner"),
                new XElement(Unattend + "Path", RunnerCommand)));
        foreach (var command in existing)
        {
            runSynchronous.Add(command);
        }
    }

    private static byte[] Serialize(XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            OmitXmlDeclaration = false,
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            document.Declaration = new XDeclaration("1.0", "utf-8", null);
            document.Save(writer);
        }
        return stream.ToArray();
    }
}
