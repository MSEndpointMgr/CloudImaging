using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CloudImaging.Contracts.Models.SetupProfiles;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>One answer file problem: a stable code, the 1-based line (0 for the whole file) and the message.</summary>
/// <param name="Code">Stable machine identifier.</param>
/// <param name="Line">Line of the offending start tag or parse error; 0 for whole-file problems.</param>
/// <param name="Message">Ready-to-show sentence.</param>
public sealed record AnswerFileProblem(string Code, int Line, string Message);

/// <summary>Problems found in an answer file, and the passes of a valid file.</summary>
/// <param name="Problems">At most 50 problems, sorted by line then code.</param>
/// <param name="Passes">Distinct configuration passes in document order.</param>
public sealed record AnswerFileValidationResult(IReadOnlyList<AnswerFileProblem> Problems, IReadOnlyList<string> Passes)
{
    /// <summary>True when the file has no problems.</summary>
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Authoritative answer file rules. The portal runs the same rules live; both are pinned by
/// tests/fixtures/answer-file-cases.json, so a rule or message changes in both places at once.
/// </summary>
public static class AnswerFileValidator
{
    /// <summary>Most problems returned.</summary>
    public const int MaxProblems = 50;

    /// <summary>Message for each rule code.</summary>
    public static IReadOnlyDictionary<string, string> Messages { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tooLarge"] = "The answer file is larger than 2 MB. Remove settings you do not need.",
        ["notWellFormed"] = "This is not well-formed XML. Check for a missing closing tag or an unescaped & or < on or before this line.",
        ["dtdNotAllowed"] = "Document type declarations are not allowed. Remove the DOCTYPE line.",
        ["notAnswerFile"] = "This is not an answer file. The root element must be unattend in the urn:schemas-microsoft-com:unattend namespace.",
        ["windowsPePass"] = "The windowsPE pass never runs, because the image is already applied. Move its settings to specialize or oobeSystem, or remove it.",
        ["offlineServicingPass"] = "The offlineServicing pass never runs, because the image is already applied. Use registry values or scripts instead, or remove it.",
        ["domainJoin"] = "Domain join is not supported. Devices join Entra ID through Autopilot. Remove the UnattendedJoin component.",
        ["skipMachineOobe"] = "SkipMachineOOBE skips OOBE, so Autopilot never starts. Remove this element.",
        ["skipUserOobe"] = "SkipUserOOBE skips OOBE, so Autopilot never starts. Remove this element.",
        ["autoLogon"] = "AutoLogon signs in a local account before Autopilot can run. Remove the AutoLogon element.",
        ["plainTextPassword"] = "Plain-text passwords are not allowed, because everyone who can read the profile could read them. Remove this password.",
        ["runnerCommandPresent"] = "This file already contains the Cloud Imaging setup runner command. Remove that RunSynchronousCommand; Cloud Imaging adds it to each device.",
        ["tooManySpecializeCommands"] = "The specialize pass has more than 499 commands. Remove at least one, because Cloud Imaging adds its own.",
    };

    /// <summary>Namespace every answer file uses.</summary>
    public const string UnattendNamespace = "urn:schemas-microsoft-com:unattend";

    /// <summary>File name of the setup runner, which only Cloud Imaging may add.</summary>
    public const string RunnerScriptName = "Invoke-CloudImagingSetup.ps1";

    private const int MaxSpecializeCommands = 499;

    /// <summary>Applies every rule to <paramref name="content"/>.</summary>
    public static AnswerFileValidationResult Validate(string content)
    {
        // A leading BOM is accepted and does not count towards the size.
        var text = content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
        if (Encoding.UTF8.GetByteCount(text) > SetupProfileContent.MaxAnswerFileBytes)
        {
            return Single("tooLarge", 0);
        }

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            // A prohibited DTD comes without a usable line, so the declaration is found in the text.
            if (DoctypeLine(text) is { } doctype && (ex.LineNumber == 0 || ex.LineNumber >= doctype))
            {
                return Single("dtdNotAllowed", doctype);
            }
            return Single("notWellFormed", Math.Max(1, ex.LineNumber));
        }

        var problems = new List<AnswerFileProblem>();
        var root = document.Root!;
        if (!Is(root, "unattend") || root.Name.NamespaceName != UnattendNamespace)
        {
            Add(problems, "notAnswerFile", root);
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            CheckElement(element, problems);
        }

        var passes = problems.Count == 0
            ? root.Descendants().Where(e => Is(e, "settings")).Select(e => Attribute(e, "pass")).OfType<string>().Distinct(StringComparer.Ordinal).ToList()
            : [];

        var sorted = problems
            .OrderBy(p => p.Line)
            .ThenBy(p => p.Code, StringComparer.Ordinal)
            .Take(MaxProblems)
            .ToList();
        return new AnswerFileValidationResult(sorted, passes);
    }

    private static void CheckElement(XElement element, List<AnswerFileProblem> problems)
    {
        if (Is(element, "settings"))
        {
            var pass = Attribute(element, "pass");
            if (string.Equals(pass, "windowsPE", StringComparison.OrdinalIgnoreCase))
            {
                Add(problems, "windowsPePass", element);
            }
            else if (string.Equals(pass, "offlineServicing", StringComparison.OrdinalIgnoreCase))
            {
                Add(problems, "offlineServicingPass", element);
            }
        }
        else if (Is(element, "component") && string.Equals(Attribute(element, "name"), "Microsoft-Windows-UnattendedJoin", StringComparison.OrdinalIgnoreCase))
        {
            Add(problems, "domainJoin", element);
        }
        else if (Is(element, "SkipMachineOOBE"))
        {
            Add(problems, "skipMachineOobe", element);
        }
        else if (Is(element, "SkipUserOOBE"))
        {
            Add(problems, "skipUserOobe", element);
        }
        else if (Is(element, "AutoLogon"))
        {
            Add(problems, "autoLogon", element);
        }
        else if (Is(element, "PlainText") && string.Equals(Text(element).Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            Add(problems, "plainTextPassword", element);
        }
        else if (Is(element, "Path") && Text(element).Contains(RunnerScriptName, StringComparison.OrdinalIgnoreCase))
        {
            Add(problems, "runnerCommandPresent", element);
        }
        else if (Is(element, "RunSynchronous") && IsSpecializeDeployment(element.Parent)
            && element.Elements().Count(e => Is(e, "RunSynchronousCommand")) > MaxSpecializeCommands)
        {
            Add(problems, "tooManySpecializeCommands", element);
        }

        // A password with a Value and no PlainText child is treated as plain text by Windows. A
        // PlainText child decides on its own line instead.
        if (element.Name.LocalName.EndsWith("Password", StringComparison.OrdinalIgnoreCase)
            && element.Elements().Any(e => Is(e, "Value"))
            && !element.Elements().Any(e => Is(e, "PlainText")))
        {
            Add(problems, "plainTextPassword", element);
        }
    }

    /// <summary>The element's own text; CDATA is ignored by every rule.</summary>
    private static string Text(XElement element) =>
        string.Concat(element.Nodes().OfType<XText>().Where(t => t is not XCData).Select(t => t.Value));

    private static bool IsSpecializeDeployment(XElement? component) =>
        component is not null
        && Is(component, "component")
        && string.Equals(Attribute(component, "name"), "Microsoft-Windows-Deployment", StringComparison.OrdinalIgnoreCase)
        && component.Parent is { } settings
        && Is(settings, "settings")
        && string.Equals(Attribute(settings, "pass"), "specialize", StringComparison.OrdinalIgnoreCase);

    private static bool Is(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static void Add(List<AnswerFileProblem> problems, string code, XElement element) =>
        problems.Add(new AnswerFileProblem(code, ((IXmlLineInfo)element).LineNumber, Messages[code]));

    private static AnswerFileValidationResult Single(string code, int line) =>
        new([new AnswerFileProblem(code, line, Messages[code])], []);

    private static int? DoctypeLine(string text)
    {
        // A DOCTYPE only counts in markup, so comments, CDATA and processing instructions are blanked first.
        var blanked = Regex.Replace(text, @"<!--.*?-->|<!\[CDATA\[.*?\]\]>|<\?.*?\?>", m => Regex.Replace(m.Value, "[^\n]", " "), RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        var index = blanked.IndexOf("<!DOCTYPE", StringComparison.Ordinal);
        return index < 0 ? null : blanked.AsSpan(0, index).Count('\n') + 1;
    }
}
