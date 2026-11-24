using System.Globalization;
using System.Text;

namespace ImagingAI.Api.Services;

internal static class MrnFormat
{
    public static bool IsValid(string value)
    {
        var length = 0;
        foreach (var character in value.EnumerateRunes())
        {
            if (++length > 50)
                return false;
            if (Rune.IsLetter(character) || Rune.GetUnicodeCategory(character) == UnicodeCategory.DecimalDigitNumber)
                continue;
            if (length == 1 || character.Value is not ('.' or '_' or '/' or '-'))
                return false;
        }

        return length > 0;
    }
}
