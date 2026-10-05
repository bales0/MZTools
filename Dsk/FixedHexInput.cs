using System;
using System.Linq;

namespace MZTools;

// Pure overwrite operations: no insertion, removal or separator changes.
internal static class FixedHexInput
{
    internal static bool IsDigit(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    internal static bool TryOverwrite(string text, int caret, string digits, out string result, out int next)
    {
        result = text; next = caret;
        if (digits.Length == 0 || digits.Any(c => !IsDigit(c))) return false;
        var positions = Enumerable.Range(Math.Clamp(caret, 0, text.Length), text.Length - Math.Clamp(caret, 0, text.Length))
            .Where(index => IsDigit(text[index])).Take(digits.Length + 1).ToArray();
        if (positions.Length < digits.Length) return false;
        char[] output = text.ToCharArray();
        for (int i = 0; i < digits.Length; i++) output[positions[i]] = char.ToUpperInvariant(digits[i]);
        result = new string(output);
        next = positions.Length > digits.Length ? positions[digits.Length] : text.Length;
        return true;
    }

    internal static bool TryPaste(string text, int caret, string pasted, out string result, out int next)
    {
        result = text; next = caret;
        string[] tokens = pasted.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Byte-oriented clipboard input, either AA BB CC or AABBCC.
        if (tokens.Length == 0 || tokens.Any(token => token.Length % 2 != 0 || token.Any(c => !IsDigit(c)))) return false;
        int start = Math.Clamp(caret, 0, text.Length);
        while (start < text.Length && !IsDigit(text[start])) start++;
        if (start == text.Length) return false;
        if (start > 0 && IsDigit(text[start - 1])) start--; // Paste begins at this byte, not its low nibble.
        return TryOverwrite(text, start, string.Concat(tokens), out result, out next);
    }

    internal static int Move(string text, int caret, int direction)
    {
        int position = Math.Clamp(caret, 0, text.Length);
        for (position += direction; position >= 0 && position < text.Length; position += direction)
            if (IsDigit(text[position])) return position;
        return direction < 0 ? 0 : text.Length;
    }
}
