namespace MyProxy.Core;

public static class PairingCodeFormatter
{
    public static (string Text, int CaretIndex) Format(string? text, int caretIndex)
    {
        string original = text ?? "";
        int originalCaret = Math.Clamp(caretIndex, 0, original.Length);
        int compactCaret = 0;
        for (int i = 0; i < originalCaret; i++)
        {
            if (original[i] is not (' ' or '-'))
            {
                compactCaret++;
            }
        }

        string compact = original.ToUpperInvariant().Replace(" ", "").Replace("-", "");
        if (compact.Length > 8)
        {
            compact = compact[..8];
        }

        compactCaret = Math.Min(compactCaret, compact.Length);
        string formatted = compact.Length > 4 ? compact.Insert(4, "-") : compact;
        int formattedCaret = compactCaret + (compact.Length > 4 && compactCaret > 4 ? 1 : 0);
        return (formatted, Math.Min(formattedCaret, formatted.Length));
    }
}
