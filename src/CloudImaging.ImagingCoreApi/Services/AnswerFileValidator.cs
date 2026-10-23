using System.Text;
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
        ["tooLarge"] = "The answer file is larger than 256 KB. Remove settings you do not need.",
        ["notWellFormed"] = "This is not well-formed XML. Check for a missing closing tag or an unescaped & or < on or before this line.",
        ["dtdNotAllowed"] = "Document type declarations are not allowed. Remove the DOCTYPE line.",
        ["notAnswerFile"] = "This is not an answer file. The root element must be unattend in the urn:schemas-microsoft-com:unattend namespace.",
        ["windowsPePass"] = "The windowsPE pass is not allowed, because Windows is already installed when this file is used. Remove this settings element.",
        ["offlineServicingPass"] = "The offlineServicing pass is not allowed. Remove this settings element and use registry values or scripts instead.",
        ["domainJoin"] = "Domain join is not allowed, because it breaks Windows Autopilot. Remove the Microsoft-Windows-UnattendedJoin component.",
        ["skipMachineOobe"] = "SkipMachineOOBE is not allowed, because it skips Windows Autopilot. Remove this element.",
        ["skipUserOobe"] = "SkipUserOOBE is not allowed, because it skips Windows Autopilot. Remove this element.",
        ["autoLogon"] = "AutoLogon is not allowed, because it signs in with a stored account. Remove the AutoLogon element.",
        ["plainTextPassword"] = "Plain-text passwords are not allowed. Remove this password.",
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
        if (Encoding.UTF8.GetByteCount(content) > SetupProfileContent.MaxAnswerFileBytes)
        {
            return Single("tooLarge", 0);
        }

        var text = content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
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
            var line = Math.Max(1, ex.LineNumber);
            return Single(IsDoctypeLine(text, line) ? "dtdNotAllowed" : "notWellFormed", line);
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
        else if (Is(element, "PlainText") && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            Add(problems, "plainTextPassword", element);
        }
        else if (Is(element, "Path") && element.Value.Contains(RunnerScriptName, StringComparison.OrdinalIgnoreCase))
        {
            Add(problems, "runnerCommandPresent", element);
        }
        else if (Is(element, "RunSynchronous") && IsSpecializeDeployment(element.Parent)
            && element.Elements().Count(e => Is(e, "RunSynchronousCommand")) > MaxSpecializeCommands)
        {
            Add(problems, "tooManySpecializeCommands", element);
        }

        // A password with a Value but without PlainText false is treated as plain text by Windows.
        // A PlainText true child is already reported on its own line.
        if (element.Name.LocalName.EndsWith("Password", StringComparison.OrdinalIgnoreCase)
            && element.Elements().Any(e => Is(e, "Value"))
            && !element.Elements().Any(e => Is(e, "PlainText") && IsBoolean(e.Value)))
        {
            Add(problems, "plainTextPassword", element);
        }
    }

    private static bool IsBoolean(string value) =>
        string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);

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

    private static bool IsDoctypeLine(string text, int line)
    {
        var lines = text.Split('\n');
        return line - 1 < lines.Length && lines[line - 1].Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
    }
}
