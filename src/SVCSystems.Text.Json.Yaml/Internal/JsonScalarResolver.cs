using System.Globalization;
using System.Numerics;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using Scalar = YamlDotNet.Core.Events.Scalar;

// Portions of this file are adapted from YamlDotNet.System.Text.Json
// (https://github.com/IvanJosipovic/YamlDotNet.System.Text.Json), MIT licensed.

namespace SVCSystems.Text.Json.Yaml.Internal;

internal enum JsonScalarKind
{
    Error,
    String,
    Null,
    Boolean,
    Integer,
    Float,
}

/// <summary>Resolves YAML scalars for the YAML 1.2.2 Core and JSON schemas.</summary>
internal static class JsonScalarResolver
{
    public static JsonScalarKind ResolveImplicit(Scalar scalar, YamlScalarSchema schema)
    {
        if (scalar.Style != ScalarStyle.Plain)
        {
            return JsonScalarKind.String;
        }

        var value = scalar.Value;

        if (IsNullScalar(value, schema))
        {
            return JsonScalarKind.Null;
        }

        if (TryResolveBoolean(value, schema, out _))
        {
            return JsonScalarKind.Boolean;
        }

        if (IsIntegerScalar(value, schema))
        {
            return JsonScalarKind.Integer;
        }

        if (IsFloatScalar(value, schema) || (schema == YamlScalarSchema.Core && IsSpecialFloat(value)))
        {
            return JsonScalarKind.Float;
        }

        return schema == YamlScalarSchema.Json ? JsonScalarKind.Error : JsonScalarKind.String;
    }

    public static bool IsNullScalar(string? value, YamlScalarSchema schema)
    {
        return schema == YamlScalarSchema.Core
            ? string.IsNullOrEmpty(value) || value is "null" or "Null" or "NULL" or "~"
            : value == "null";
    }

    public static bool TryResolveBoolean(string? value, YamlScalarSchema schema, out bool result)
    {
        switch (value)
        {
            case "true":
                result = true;
                return true;
            case "false":
                result = false;
                return true;
            case "True" or "TRUE" when schema == YamlScalarSchema.Core:
                result = true;
                return true;
            case "False" or "FALSE" when schema == YamlScalarSchema.Core:
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    public static bool IsIntegerScalar(string? value, YamlScalarSchema schema)
    {
        if (schema == YamlScalarSchema.Json)
        {
            return IsJsonInteger(value);
        }

        if (IsSignedDigits(value))
        {
            return true;
        }

        return IsRadixInteger(value, "0x", "0X", IsHexDigit)
            || IsRadixInteger(value, "0o", "0O", c => c is >= '0' and <= '7');
    }

    public static bool IsFloatScalar(string? value, YamlScalarSchema schema)
    {
        return schema == YamlScalarSchema.Json
            ? IsJsonSchemaFloat(value)
            : IsCoreFloat(value);
    }

    public static bool TryResolveTaggedBoolean(string value, YamlScalarSchema schema, out bool result)
    {
        return TryResolveBoolean(value, schema, out result);
    }

    public static bool IsSpecialFloat(string? value)
    {
        return value is ".nan" or ".NaN" or ".NAN"
            or "+.nan" or "+.NaN" or "+.NAN"
            or "-.nan" or "-.NaN" or "-.NAN"
            or ".inf" or ".Inf" or ".INF"
            or "+.inf" or "+.Inf" or "+.INF"
            or "-.inf" or "-.Inf" or "-.INF";
    }

    public static bool NeedsQuoting(string? value, YamlScalarSchema schema = YamlScalarSchema.Core)
    {
        // The JSON schema has no plain-string fallback: unmatched plain scalars are errors.
        if (schema == YamlScalarSchema.Json) return true;

        if (string.IsNullOrEmpty(value)
            || IsNullScalar(value, schema)
            || TryResolveBoolean(value, schema, out _)
            || IsIntegerScalar(value, schema)
            || IsFloatScalar(value, schema))
        {
            return true;
        }

        return schema == YamlScalarSchema.Core && IsSpecialFloat(value);
    }

    public static string NormalizeInteger(string value)
    {
        var sign = 1;
        var start = 0;

        if (value[0] is '-' or '+')
        {
            sign = value[0] == '-' ? -1 : 1;
            start = 1;
        }

        var digits = value.AsSpan(start);

        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var number = new BigInteger(0);
            foreach (var c in digits[2..])
            {
                number = number * 16 + (c is >= '0' and <= '9' ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10);
            }

            return (sign * number).ToString(CultureInfo.InvariantCulture);
        }

        if (digits.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            var number = new BigInteger(0);
            foreach (var c in digits[2..])
            {
                number = number * 8 + c - '0';
            }

            return (sign * number).ToString(CultureInfo.InvariantCulture);
        }

        var decimalValue = BigInteger.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return decimalValue.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Returns valid JSON number text equivalent to a schema-resolved YAML float.</summary>
    public static string NormalizeFloat(string value)
    {
        var start = value[0] is '-' or '+' ? 1 : 0;
        var negative = value[0] == '-';
        var exponentIndex = FindExponent(value, start);
        var mantissaEnd = exponentIndex < 0 ? value.Length : exponentIndex;
        var dotIndex = value.IndexOf('.', start, mantissaEnd - start);
        var integerEnd = dotIndex < 0 ? mantissaEnd : dotIndex;
        var integerStart = start;

        while (integerStart < integerEnd - 1 && value[integerStart] == '0')
        {
            integerStart++;
        }

        var integerLength = integerEnd - integerStart;
        var fractionStart = dotIndex < 0 ? mantissaEnd : dotIndex + 1;
        var fractionLength = mantissaEnd - fractionStart;
        var builder = new System.Text.StringBuilder(value.Length + 2);

        if (negative)
        {
            builder.Append('-');
        }

        if (integerLength == 0)
        {
            builder.Append('0');
        }
        else
        {
            builder.Append(value, integerStart, integerLength);
        }

        if (dotIndex >= 0 || exponentIndex < 0)
        {
            builder.Append('.');
            if (fractionLength == 0)
            {
                builder.Append('0');
            }
            else
            {
                builder.Append(value, fractionStart, fractionLength);
            }
        }

        if (exponentIndex >= 0)
        {
            builder.Append(value.AsSpan(exponentIndex));
        }

        return builder.ToString();
    }

    /// <summary>Canonical decimal identity used for YAML float mapping keys.</summary>
    public static string CanonicalFloat(string value)
    {
        var normalized = NormalizeFloat(value);
        var index = normalized[0] == '-' ? 1 : 0;
        var exponentIndex = FindExponent(normalized, index);
        var mantissaEnd = exponentIndex < 0 ? normalized.Length : exponentIndex;
        var dotIndex = normalized.IndexOf('.', index, mantissaEnd - index);
        var fractionDigits = dotIndex < 0 ? 0 : mantissaEnd - dotIndex - 1;
        var digits = new System.Text.StringBuilder(mantissaEnd - index);

        for (var i = index; i < mantissaEnd; i++)
        {
            if (normalized[i] != '.')
            {
                digits.Append(normalized[i]);
            }
        }

        var allDigits = digits.ToString();
        var firstNonZero = 0;
        while (firstNonZero < allDigits.Length && allDigits[firstNonZero] == '0')
        {
            firstNonZero++;
        }

        if (firstNonZero == allDigits.Length)
        {
            return "0";
        }

        var last = allDigits.Length;
        while (last > firstNonZero && allDigits[last - 1] == '0')
        {
            last--;
        }

        var exponent = exponentIndex < 0
            ? BigInteger.Zero
            : BigInteger.Parse(normalized.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        exponent -= fractionDigits;
        exponent += allDigits.Length - last;

        return (normalized[0] == '-' ? "-" : "")
            + allDigits[firstNonZero..last]
            + "e"
            + exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsSignedDigits(string? value)
    {
        if (value is not { Length: > 0 }) return false;
        var start = value[0] is '-' or '+' ? 1 : 0;
        if (start == value.Length) return false;
        for (var i = start; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9') return false;
        }
        return true;
    }

    private static bool IsJsonInteger(string? value)
    {
        if (value is not { Length: > 0 }) return false;
        var start = value[0] == '-' ? 1 : 0;
        if (start == value.Length) return false;
        if (value[start] == '0') return start + 1 == value.Length;
        if (value[start] is < '1' or > '9') return false;
        for (var i = start + 1; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9') return false;
        }
        return true;
    }

    private static bool IsJsonSchemaFloat(string? value)
    {
        if (value is not { Length: > 0 }) return false;
        var i = value[0] == '-' ? 1 : 0;
        if (i == value.Length) return false;

        if (value[i] == '0')
        {
            i++;
        }
        else if (value[i] is >= '1' and <= '9')
        {
            do { i++; } while (i < value.Length && value[i] is >= '0' and <= '9');
        }
        else
        {
            return false;
        }

        var hasFractionOrExponent = false;
        if (i < value.Length && value[i] == '.')
        {
            hasFractionOrExponent = true;
            i++;
            while (i < value.Length && value[i] is >= '0' and <= '9') i++;
        }

        if (i < value.Length && value[i] is 'e' or 'E')
        {
            hasFractionOrExponent = true;
            i++;
            if (i < value.Length && value[i] is '+' or '-') i++;
            var exponentStart = i;
            while (i < value.Length && value[i] is >= '0' and <= '9') i++;
            if (i == exponentStart) return false;
        }

        return hasFractionOrExponent && i == value.Length;
    }

    private static bool IsCoreFloat(string? value)
    {
        if (value is not { Length: > 0 }) return false;
        var start = value[0] is '-' or '+' ? 1 : 0;
        if (start == value.Length) return false;

        var exponentIndex = FindExponent(value, start);
        var mantissaEnd = exponentIndex < 0 ? value.Length : exponentIndex;
        var dotIndex = value.IndexOf('.', start, mantissaEnd - start);
        var digitCount = 0;

        for (var i = start; i < mantissaEnd; i++)
        {
            if (value[i] is >= '0' and <= '9') digitCount++;
            else if (value[i] != '.' || i != dotIndex) return false;
        }

        if (digitCount == 0 || (dotIndex < 0 && exponentIndex < 0)) return false;
        if (exponentIndex < 0) return true;

        var iExponent = exponentIndex + 1;
        if (iExponent < value.Length && value[iExponent] is '+' or '-') iExponent++;
        if (iExponent == value.Length) return false;
        for (var i = iExponent; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9') return false;
        }
        return true;
    }

    private static bool IsRadixInteger(string? value, string lowerPrefix, string upperPrefix, Func<char, bool> isDigit)
    {
        if (value is not { Length: > 2 }) return false;
        var start = value[0] is '-' or '+' ? 1 : 0;
        var span = value.AsSpan(start);
        if (!span.StartsWith(lowerPrefix) && !span.StartsWith(upperPrefix)) return false;
        var digits = span[2..];
        if (digits.IsEmpty) return false;
        foreach (var c in digits)
        {
            if (!isDigit(c)) return false;
        }
        return true;
    }

    private static int FindExponent(string value, int start)
    {
        for (var i = start; i < value.Length; i++)
        {
            if (value[i] is 'e' or 'E') return i;
        }
        return -1;
    }

    private static bool IsHexDigit(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
