using System.Text;
using System.Text.RegularExpressions;

namespace JavaToCSharp.Utilities;

/// <summary>
/// Converts Java <c>printf</c> / <c>String.format</c> format specifiers to C#
/// <see cref="string.Format"/> composite format strings.
/// </summary>
internal static partial class FormatStringTranslator
{
    /// <summary>
    /// Matches a single Java printf format specifier.
    /// Group 1: positional index (e.g. <c>1$</c>) — optional.
    /// Group 2: flags (<c>-</c>, <c>+</c>, <c> </c>, <c>0</c>, <c>#</c>).
    /// Group 3: width (digits or <c>*</c>).
    /// Group 4: precision (digits or <c>*</c>).
    /// Group 5: conversion character.
    /// </summary>
    [GeneratedRegex(@"%(?:n|(?<pos>\d+\$)?(?<flags>[\-+ 0#]*)(?<width>\d+|\*)?(?:\.(?<prec>\d+|\*))?(?<conv>[tTbBhHsScCdoxXeEfgGta%]))")]
    private static partial Regex SpecRegex();

    /// <summary>
    /// Converts a Java format string to a C# composite format string using sequential indexing.
    /// </summary>
    public static string ConvertJavaToCSharp(string javaFormat)
    {
        return ConvertWithArgCount(javaFormat, 0).csharpFormat;
    }

    /// <summary>
    /// Converts a Java format string and returns both the C# format string and the
    /// number of arguments consumed (sequential auto-indexed).
    /// </summary>
    public static (string csharpFormat, int argCount) ConvertWithArgCount(string javaFormat, int startArgIndex = 0)
    {
        if (string.IsNullOrEmpty(javaFormat))
            return (javaFormat, 0);

        var result = new StringBuilder(javaFormat.Length + 16);
        var autoIndex = startArgIndex;
        var maxExplicitIndex = startArgIndex - 1;

        var lastEnd = 0;
        foreach (Match match in SpecRegex().Matches(javaFormat))
        {
            result.Append(javaFormat, lastEnd, match.Index - lastEnd);
            lastEnd = match.Index + match.Length;

            var conv = match.Groups["conv"].Value;
            if (conv == "%")
            {
                result.Append('%');
                continue;
            }

            if (!match.Groups["conv"].Success)
            {
                // %n (newline) — no conv group captured
                result.Append('\n');
                continue;
            }

            int argIdx;
            if (match.Groups["pos"].Success)
            {
                var posStr = match.Groups["pos"].Value.TrimEnd('$');
                if (int.TryParse(posStr, out var pos))
                {
                    argIdx = pos - 1;
                    if (argIdx > maxExplicitIndex)
                        maxExplicitIndex = argIdx;
                }
                else
                {
                    argIdx = autoIndex++;
                }
            }
            else
            {
                argIdx = autoIndex++;
            }

            var (alignment, format) = BuildCSharpFormat(match);
            result.Append('{');
            result.Append(argIdx);
            if (alignment.Length > 0)
            {
                result.Append(',');
                result.Append(alignment);
            }
            if (format.Length > 0)
            {
                result.Append(':');
                result.Append(format);
            }
            result.Append('}');
        }

        result.Append(javaFormat, lastEnd, javaFormat.Length - lastEnd);

        var consumed = Math.Max(autoIndex - startArgIndex, maxExplicitIndex - startArgIndex + 1);
        if (consumed < 0) consumed = 0;
        return (result.ToString(), consumed);
    }

    private static (string alignment, string format) BuildCSharpFormat(Match match)
    {
        var flags = match.Groups["flags"].Value;
        var widthStr = match.Groups["width"].Value;
        var precStr = match.Groups["prec"].Value;
        var conv = match.Groups["conv"].Value;

        int.TryParse(widthStr, out var width);
        int.TryParse(precStr, out var prec);

        var hasLeftAlign = flags.Contains('-');

        // Build alignment string (C#: {index,alignment:format})
        var alignment = "";
        if (width > 0 && !(flags.Contains('0') && !hasLeftAlign && conv is "d" or "o" or "x" or "X"))
        {
            alignment = hasLeftAlign ? (-width).ToString() : width.ToString();
        }

        // Build format string (C# format specifier after colon)
        var fmt = new StringBuilder();

        switch (conv)
        {
            case "d" or "o" or "x" or "X" or "b" or "B" or "h" or "H":
                if (conv is "x" or "X")
                {
                    fmt.Append(conv == "x" ? "x" : "X");
                    if (prec > 0) fmt.Append(prec);
                }
                else if (conv is "o")
                {
                    fmt.Append('O');
                    if (prec > 0) fmt.Append(prec);
                }
                else if (conv is "b" or "B")
                {
                    fmt.Append('S');
                }
                else if (conv is "h" or "H")
                {
                    fmt.Append('X');
                }
                else
                {
                    fmt.Append('D');
                    if (flags.Contains('0') && !hasLeftAlign && width > 0)
                    {
                        fmt.Append(width);
                    }
                }

                if (flags.Contains('+'))
                    fmt.Append(";+;-;0");

                break;

            case "f" or "F":
                fmt.Append('F');
                if (prec > 0) fmt.Append(prec);
                break;

            case "e" or "E":
                fmt.Append(conv == "e" ? "e" : "E");
                if (prec > 0) fmt.Append(prec);
                break;

            case "g" or "G":
                fmt.Append(conv == "g" ? "g" : "G");
                break;

            case "s" or "S" or "c" or "C":
                break;

            case "a" or "A":
                fmt.Append(conv == "a" ? "a" : "A");
                break;

            case "t" or "T":
                fmt.Append(conv);
                break;
        }

        return (alignment, fmt.ToString());
    }
}
