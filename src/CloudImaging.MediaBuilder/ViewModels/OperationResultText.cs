using System.Text.RegularExpressions;
using System.Windows;

namespace CloudImaging.MediaBuilder.ViewModels;

/// <summary>Text and clipboard helpers shared by the success/failure result dialogs.</summary>
public static class OperationResultText
{
    private static readonly Regex EmbeddedReferenceRegex = new(
        @"\s*\(?Error reference:\s*(?<code>CMB-[A-Z0-9]+-[A-Z0-9]+-\d+)\)?\.?", RegexOptions.CultureInvariant);

    /// <summary>
    /// Separates a support reference code a service already embedded in <paramref name="message"/>
    /// from the readable reason, or creates a new one when there is none.
    /// </summary>
    public static (string Message, string Reference) SplitSupportReference(string message, Func<string> createReference)
    {
        var match = EmbeddedReferenceRegex.Match(message);
        if (!match.Success)
            return (message.Trim(), createReference());

        var reason = message.Remove(match.Index, match.Length).Trim();
        return (reason.Length > 0 ? reason : message.Trim(), match.Groups["code"].Value);
    }

    /// <summary>Title, message and reference as one block, for pasting into a support ticket.</summary>
    public static string ErrorDetails(string title, string? message, string? reference) => reference is null
        ? $"{title}{Environment.NewLine}{message}"
        : $"{title}{Environment.NewLine}{message}{Environment.NewLine}Reference: {reference}";

    /// <summary>Copies <paramref name="text"/> to the clipboard; never throws.</summary>
    public static void TryCopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        // The clipboard can be briefly locked by another process; copying is a convenience, never fatal.
        try { Clipboard.SetText(text); } catch { /* best-effort */ }
    }
}
