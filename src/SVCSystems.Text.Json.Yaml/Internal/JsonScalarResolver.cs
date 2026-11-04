using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using Scalar = YamlDotNet.Core.Events.Scalar;

// Portions of this file are adapted from YamlDotNet.System.Text.Json
// (https://github.com/IvanJosipovic/YamlDotNet.System.Text.Json), MIT licensed.

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// Resolves YAML plain scalars into JSON token semantics.
/// </summary>
/// <remarks>
/// YAML 1.1 and YAML 1.2 disagree on several scalar classes (for example <c>yes</c>, <c>on</c> and
/// <c>~</c>). This resolver implements the YAML 1.2 core schema (the default
/// <see cref="YamlScalarSchema.Core"/>):
/// <list type="bullet">
///   <item><description>Booleans are <c>true</c>/<c>false</c> in any capitalization; <c>yes</c>, <c>no</c>, <c>on</c> and <c>off</c> are strings.</description></item>
///   <item><description>Null is an empty scalar, <c>null</c> (any capitalization) or <c>~</c>.</description></item>
///   <item><description>Integers are optionally signed decimal digits; under the Core schema hexadecimal (<c>0x1A</c>) and octal (<c>0o17</c>) forms also resolve to integers. Leading zeros are interpreted as decimal (YAML 1.1 octal such as <c>00123</c> is parsed as the decimal number <c>123</c>); sexagesimal notation is a string.</description></item>
///   <item><description>Floats are decimal numbers with optional exponent; <c>.nan</c> and <c>.inf</c> have no JSON representation and follow <see cref="SpecialFloatBehavior"/>.</description></item>
///   <item><description>Timestamps such as <c>2026-01-01</c> have no JSON equivalent and remain strings.</description></item>
///   <item><description>Quoted scalars are always strings.</description></item>
/// </list>
/// All numeric parsing is culture invariant.
/// </remarks>
internal static class JsonScalarResolver
{
    /// <summary>
    /// Determines whether the plain scalar resolves to JSON <c>null</c>.
    /// </summary>
    public static bool IsNullScalar(string? value)
    {
        return string.IsNullOrEmpty(value)
            || value is "null" or "Null" or "NULL" or "~";
    }

    /// <summary>
    /// Determines whether the plain scalar resolves to a JSON boolean.
    /// </summary>
    public static bool TryResolveBoolean(string? value, out bool result)
    {
        switch (value)
        {
            case "true" or "True" or "TRUE":
                result = true;
                return true;
            case "false" or "False" or "FALSE":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    /// <summary>
    /// Determines whether the plain scalar resolves to a JSON integer.
    /// </summary>
    public static bool IsIntegerScalar(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return false;
        }

        var start = value[0] is '-' or '+' ? 1 : 0;

        if (start == value.Length)
        {
            return false;
        }

        for (var i = start; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether the plain scalar resolves to a JSON floating point number.
    /// </summary>
    public static bool IsFloatScalar(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return false;
        }

        var start = value[0] is '-' or '+' ? 1 : 0;

        if (start == value.Length)
        {
            return false;
        }

        var mantissaDigits = 0;
        var exponentDigits = 0;
        var sawDot = false;
        var sawExponent = false;

        for (var i = start; i < value.Length; i++)
        {
            var c = value[i];

            if (c is >= '0' and <= '9')
            {
                if (sawExponent)
                {
                    exponentDigits++;
                }
                else
                {
                    mantissaDigits++;
                }
            }
            else if (c == '.' && !sawDot && !sawExponent)
            {
                sawDot = true;
            }
            else if ((c == 'e' || c == 'E') && !sawExponent && mantissaDigits > 0)
            {
                sawExponent = true;
            }
            else if ((c == '-' || c == '+') && sawExponent && exponentDigits == 0 && (value[i - 1] == 'e' || value[i - 1] == 'E'))
            {
                // exponent sign
            }
            else
            {
                return false;
            }
        }

        // Pure integers are handled by IsIntegerScalar; floats require a dot or an exponent.
        return (sawDot || sawExponent) && mantissaDigits > 0 && (exponentDigits > 0 || !sawExponent);
    }

    /// <summary>
    /// Determines whether a JSON string written as a plain YAML scalar could be misinterpreted
    /// as a non-string value when parsed again with the given schema.
    /// </summary>
    public static bool NeedsQuoting(string? value, YamlScalarSchema schema = YamlScalarSchema.Core)
    {
        return string.IsNullOrEmpty(value)
            || IsNullScalar(value)
            || TryResolveBoolean(value, out _)
            || IsIntegerScalar(value)
            || IsFloatScalar(value)
            || (schema == YamlScalarSchema.Core && (IsHexIntegerScalar(value) || IsOctalIntegerScalar(value)));
    }

    /// <summary>
    /// Throws when the scalar uses a YAML 1.1 special float that has no lossless JSON representation.
    /// </summary>
    public static void RejectSpecialFloats(Scalar scalar)
    {
        if (scalar.Style == ScalarStyle.Plain && IsSpecialFloat(scalar.Value))
        {
            throw new YamlException(scalar.Start, scalar.End,
                $"The scalar '{scalar.Value}' is not supported because it has no JSON number representation. Quote the value ('\"{scalar.Value}\"') to treat it as a string.");
        }
    }

    /// <summary>
    /// Determines whether the plain scalar is a YAML special float (<c>.inf</c>, <c>.nan</c> and
    /// their signed and capitalized forms), which has no JSON number representation.
    /// </summary>
    public static bool IsSpecialFloat(string? value)
    {
        return value is ".nan" or ".NaN" or ".NAN" or
            ".inf" or ".Inf" or ".INF" or
            "+.inf" or "+.Inf" or "+.INF" or
            "-.inf" or "-.Inf" or "-.INF";
    }

    /// <summary>
    /// Determines whether the plain scalar is a hexadecimal integer per the YAML 1.2 core schema
    /// (<c>0x1A</c>, <c>-0x1A</c>).
    /// </summary>
    public static bool IsHexIntegerScalar(string? value)
    {
        return IsRadixIntegerScalar(value, "0x", "0X", IsHexDigit);
    }

    /// <summary>
    /// Determines whether the plain scalar is an octal integer per the YAML 1.2 core schema
    /// (<c>0o17</c>, <c>-0o17</c>).
    /// </summary>
    public static bool IsOctalIntegerScalar(string? value)
    {
        return IsRadixIntegerScalar(value, "0o", "0O", c => c is >= '0' and <= '7');
    }

    private static bool IsRadixIntegerScalar(string? value, string lowerPrefix, string upperPrefix, Func<char, bool> isDigit)
    {
        if (value is not { Length: > 2 })
        {
            return false;
        }

        var start = value[0] is '-' or '+' ? 1 : 0;

        if (!value.AsSpan(start).StartsWith(lowerPrefix) && !value.AsSpan(start).StartsWith(upperPrefix))
        {
            return false;
        }

        var digits = value.AsSpan(start + 2);

        if (digits.Length == 0)
        {
            return false;
        }

        foreach (var c in digits)
        {
            if (!isDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHexDigit(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>
    /// Determines whether the plain scalar resolves to a floating point number under the YAML 1.2
    /// core schema, which additionally allows forms such as <c>.5</c> and <c>1.</c>.
    /// </summary>
    public static bool IsCoreFloatScalar(string? value)
    {
        return IsFloatScalar(value) || IsMantissaOnlyFloatScalar(value);
    }

    /// <summary>
    /// Matches the core schema float forms that consist of a mantissa with a dot but no exponent:
    /// <c>.5</c>, <c>-.5</c>, <c>+5.</c> and so on. <see cref="double"/> and <see cref="decimal"/>
    /// parse these forms natively.
    /// </summary>
    private static bool IsMantissaOnlyFloatScalar(string? value)
    {
        if (value is not { Length: > 1 })
        {
            return false;
        }

        var start = value[0] is '-' or '+' ? 1 : 0;
        var digits = value.AsSpan(start);

        // The mantissa must contain at least one digit and exactly one dot: ".5" or "5.".
        var dotCount = 0;
        var digitCount = 0;

        foreach (var c in digits)
        {
            if (c is >= '0' and <= '9')
            {
                digitCount++;
            }
            else if (c == '.')
            {
                dotCount++;
            }
            else
            {
                return false;
            }
        }

        return digitCount > 0 && dotCount == 1;
    }

    /// <summary>
    /// Parses a float scalar using invariant culture, preferring <see cref="decimal"/> to preserve precision.
    /// </summary>
    public static bool TryParseNumber(string value, out decimal decimalValue, out double doubleValue)
    {
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimalValue))
        {
            doubleValue = 0;
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleValue))
        {
            return true;
        }

        return false;
    }
}
