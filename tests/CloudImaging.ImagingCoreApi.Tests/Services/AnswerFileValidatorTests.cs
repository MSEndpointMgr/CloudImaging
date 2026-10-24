using System.IO;
using System.Text;
using System.Text.Json;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

/// <summary>
/// Imaging Core's answer file rules against the case file the portal's live check also reads, so a
/// file the dialog accepts is never refused on save and the other way round.
/// </summary>
public sealed class AnswerFileValidatorTests
{
    private static readonly JsonElement Fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "answer-file-cases.json"))).RootElement;

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Fixture.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }
        return data;
    }

    private static JsonElement Case(string name) =>
        Fixture.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static string Generate(JsonElement spec)
    {
        var count = spec.GetProperty("count").GetInt32();
        List<string> lines = ["<?xml version=\"1.0\"?>", "<unattend xmlns=\"urn:schemas-microsoft-com:unattend\">"];
        if (spec.GetProperty("kind").GetString() == "repeat")
        {
            var element = spec.GetProperty("element").GetString();
            lines.AddRange(Enumerable.Range(0, count).Select(_ => $"  <{element}>true</{element}>"));
            lines.Add("</unattend>");
            return string.Join('\n', lines);
        }
        lines.Add("  <settings pass=\"specialize\">");
        lines.Add("    <component name=\"Microsoft-Windows-Deployment\">");
        lines.Add("      <RunSynchronous>");
        lines.AddRange(Enumerable.Range(1, count).Select(i => $"        <RunSynchronousCommand><Order>{i}</Order><Path>a.cmd</Path></RunSynchronousCommand>"));
        lines.AddRange(["      </RunSynchronous>", "    </component>", "  </settings>", "</unattend>"]);
        return string.Join('\n', lines);
    }

    private static string Xml(JsonElement c)
    {
        var xml = c.TryGetProperty("xml", out var text) ? text.GetString()! : Generate(c.GetProperty("generate"));
        if (c.TryGetProperty("padToBytes", out var bytes))
        {
            var missing = bytes.GetInt32() - Encoding.UTF8.GetByteCount(xml) - 7;
            var at = xml.LastIndexOf("</unattend>", StringComparison.Ordinal);
            xml = $"{xml[..at]}<!--{new string('x', missing)}-->{xml[at..]}";
        }
        return xml;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void SharedCase_GivesTheExpectedCodesAndLines(string name)
    {
        var c = Case(name);

        var result = AnswerFileValidator.Validate(Xml(c));

        var expected = c.GetProperty("expected");
        if (expected.ValueKind == JsonValueKind.String)
        {
            var first = c.GetProperty("firstLine").GetInt32();
            result.Problems.Select(p => p.Line).Should().Equal(Enumerable.Range(first, AnswerFileValidator.MaxProblems));
        }
        else
        {
            result.Problems.Select(p => (p.Code, p.Line)).Should().Equal(expected.EnumerateArray().Select(e => (e.GetProperty("code").GetString()!, e.GetProperty("line").GetInt32())));
        }
        result.Problems.Where(p => p.Message != AnswerFileValidator.Messages[p.Code]).Should().BeEmpty();
        if (c.TryGetProperty("passes", out var passes))
        {
            result.Passes.Should().Equal(passes.EnumerateArray().Select(p => p.GetString()));
        }
    }

    [Fact]
    public void EveryMessage_MatchesTheSharedCaseFile()
    {
        var shared = Fixture.GetProperty("messages").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());

        AnswerFileValidator.Messages.Should().BeEquivalentTo(shared);
    }
}
