namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Normalizes visually-confusable Unicode characters that routinely end up in operator-typed
/// catalog metadata (e.g. an OS image's <c>version</c>) via copy-paste from web pages. Microsoft's
/// own ISO download pages label files like "...23H2 <c>×</c>64" using U+00D7 MULTIPLICATION SIGN
/// rather than the ASCII letter 'x' (U+0078); pasted verbatim, that renders as a subtly different
/// glyph in the portal's UI font and reads oddly to operators who never notice the swap.
/// </summary>
public static class TextNormalization
{
    /// <summary>Replaces characters that are commonly mistaken for ASCII 'x' with 'x' itself.</summary>
    public static string? NormalizeLookalikes(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value
            .Replace('\u00D7', 'x')  // × MULTIPLICATION SIGN
            .Replace('\u2715', 'x')  // ✕ MULTIPLICATION X
            .Replace('\u2716', 'x'); // ✖ HEAVY MULTIPLICATION X
    }
}
