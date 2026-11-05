namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>The writing direction of a piece of text.</summary>
public enum TextFlowDirection
{
    /// <summary>Left-to-right (default).</summary>
    LeftToRight = 0,

    /// <summary>Right-to-left (Arabic/Persian script).</summary>
    RightToLeft = 1,
}

/// <summary>
/// Pure text-direction detection for the composer: RightToLeft when the first strong letter is
/// in the Arabic/Persian Unicode ranges (U+0600–U+06FF, U+0750–U+077F, U+FB50–U+FDFF,
/// U+FE70–U+FEFF), LeftToRight otherwise. Digits, spaces and punctuation are neutral.
/// </summary>
public static class TextDirection
{
    /// <summary>Detects the direction of the text; empty, null and neutral-only text are LeftToRight.</summary>
    /// <param name="text">The text to inspect; may be null.</param>
    public static TextFlowDirection Detect(string? text)
    {
        foreach (var character in text ?? string.Empty)
        {
            if (IsArabicScript(character))
                return TextFlowDirection.RightToLeft;

            if (char.IsLetter(character))
                return TextFlowDirection.LeftToRight;
        }

        return TextFlowDirection.LeftToRight;
    }

    private static bool IsArabicScript(char character)
        => character is (>= '\u0600' and <= '\u06FF')
            or (>= '\u0750' and <= '\u077F')
            or (>= '\uFB50' and <= '\uFDFF')
            or (>= '\uFE70' and <= '\uFEFF');
}
