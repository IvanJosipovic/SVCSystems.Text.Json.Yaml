using System.Globalization;
using System.Numerics;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using Scalar = YamlDotNet.Core.Events.Scalar;

// Portions of this file are adapted from YamlDotNet.System.Text.Json
// (https://github.com/IvanJosipovic/YamlDotNet.System.Text.Json), MIT licensed.

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// Converts a YAML event stream into JSON tokens written to a <see cref="Utf8JsonWriter"/>.
/// </summary>
/// <remarks>
/// The converter supports the JSON compatible subset of YAML plus the structural features of the
/// YAML 1.2 specification that map losslessly into the JSON data model: anchors and aliases
/// (expanded by <see cref="AliasExpandingParser"/>), merge keys, multi document streams, explicit
/// keys, and the standard YAML tags (<c>!!str</c>, <c>!!int</c>, <c>!!float</c>, <c>!!bool</c>,
/// <c>!!null</c>, and optionally <c>!!binary</c>/<c>!!timestamp</c>). Comments are skipped; they
/// have no effect on the data model. Custom tags are rejected or ignored according to
/// <see cref="YamlSerializerOptions.UnknownTags"/>; tags never cause .NET type construction.
/// <para>
/// All limits (<see cref="YamlSerializerOptions.MaxDepth"/>, <see cref="YamlSerializerOptions.MaxScalarLength"/>)
/// are enforced here so failures surface as <see cref="YamlException"/> with line/column
/// positions.
/// </para>
/// </remarks>
internal static class YamlToJsonConverter
{
    private const string TagPrefix = "tag:yaml.org,2002:";

    private const string StrTag = TagPrefix + "str";
    private const string NullTag = TagPrefix + "null";
    private const string BoolTag = TagPrefix + "bool";
    private const string IntTag = TagPrefix + "int";
    private const string FloatTag = TagPrefix + "float";
    private const string BinaryTag = TagPrefix + "binary";
    private const string TimestampTag = TagPrefix + "timestamp";

    /// <summary>Reads parsing events from a live parser or a buffered node.</summary>
    private abstract class EventSource
    {
        public abstract ParsingEvent? Read();
    }

    private sealed class LiveEventSource(IParser parser) : EventSource
    {
        public override ParsingEvent? Read()
        {
            // The parser holds the next unconsumed event in Current (the first event of a node is
            // typically already peeked by the document handling). Consume Current and advance so
            // the next Read observes the following event.
            ParsingEvent? evt;

            if (parser.Current is { } current)
            {
                evt = current;
            }
            else
            {
                if (!parser.MoveNext())
                {
                    return null;
                }

                evt = parser.Current!;
            }

            parser.MoveNext();
            return evt;
        }
    }

    private sealed class BufferedEventSource(List<ParsingEvent> events) : EventSource
    {
        private int _index;

        public override ParsingEvent? Read()
        {
            if (_index >= events.Count)
            {
                return null;
            }

            return events[_index++];
        }
    }

    /// <summary>Per mapping level explicit key tracking, used for the strict duplicate check and
    /// to give explicit keys precedence over merged keys.</summary>
    private sealed class MappingKeys
    {
        public HashSet<string> Explicit { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Context
    {
        public Context(YamlSerializerOptions? options)
        {
            AllowDuplicateKeys = options?.AllowDuplicateKeys ?? false;
            AllowMergeKeys = options?.AllowMergeKeys ?? true;
            ScalarSchema = options?.ScalarSchema ?? YamlScalarSchema.Core;
            SpecialFloats = options?.SpecialFloats ?? SpecialFloatBehavior.Reject;
            UnknownTags = options?.UnknownTags ?? UnknownTagBehavior.Reject;
            AllowBinaryTag = options?.AllowBinaryTag ?? false;
            AllowTimestampTag = options?.AllowTimestampTag ?? false;
            MaxDepth = options?.MaxDepth ?? YamlSerializerOptions.DefaultMaxDepth;
            MaxScalarLength = options?.MaxScalarLength ?? 0;
        }

        public bool AllowDuplicateKeys { get; }
        public bool AllowMergeKeys { get; }
        public YamlScalarSchema ScalarSchema { get; }
        public SpecialFloatBehavior SpecialFloats { get; }
        public UnknownTagBehavior UnknownTags { get; }
        public bool AllowBinaryTag { get; }
        public bool AllowTimestampTag { get; }
        public int MaxDepth { get; }
        public int MaxScalarLength { get; }

        public int Depth { get; private set; }
        public List<MappingKeys> KeyStack { get; } = [];

        public void EnterCollection(ParsingEvent evt)
        {
            if (Depth >= MaxDepth)
            {
                throw new YamlException(evt.Start, evt.End,
                    $"The YAML input exceeds the maximum nesting depth of {MaxDepth}. Adjust {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.MaxDepth)} if a deeper document is expected.");
            }

            Depth++;
        }

        public void ExitCollection() => Depth--;

        public MappingKeys PushMapping()
        {
            var keys = new MappingKeys();
            KeyStack.Add(keys);
            return keys;
        }

        public void PopMapping() => KeyStack.RemoveAt(KeyStack.Count - 1);

        public MappingKeys CurrentKeys => KeyStack[^1];
    }

    /// <summary>
    /// Converts the first document of the stream into JSON. Additional documents produce a
    /// <see cref="YamlException"/>.
    /// </summary>
    public static void Convert(IParser parser, Utf8JsonWriter writer, YamlSerializerOptions? options = null)
    {
        if (!ConvertNextDocument(parser, writer, options))
        {
            throw new YamlException("The YAML input does not contain a document.");
        }

        if (parser.Current is DocumentStart)
        {
            throw new YamlException("Multiple YAML documents are not supported. Provide a single document per call.");
        }
    }

    /// <summary>
    /// Converts the next document of the stream into JSON. Returns <see langword="false"/> when
    /// the stream contains no further document.
    /// </summary>
    public static bool ConvertNextDocument(IParser parser, Utf8JsonWriter writer, YamlSerializerOptions? options = null)
    {
        var context = new Context(options);
        return ConvertNextDocument(parser, writer, context);
    }

    private static bool ConvertNextDocument(IParser parser, Utf8JsonWriter writer, Context context)
    {
        // Accept<T> throws EndOfStreamException when the parser is drained; end the stream here
        // instead.
        if (parser.Current is null && !parser.MoveNext())
        {
            return false;
        }

        parser.TryConsume<StreamStart>(out _);

        if (!parser.TryConsume<DocumentStart>(out _))
        {
            return false;
        }

        if (parser.TryConsume<DocumentEnd>(out _))
        {
            // An empty document (e.g. '---') deserializes to JSON null.
            writer.WriteNullValue();
        }
        else
        {
            ConvertNode(new LiveEventSource(parser), writer, context);
            ConsumeDocumentEnd(parser);
        }

        parser.TryConsume<StreamEnd>(out _);
        return true;
    }

    private static void ConsumeDocumentEnd(IParser parser)
    {
        if (!parser.TryConsume<DocumentEnd>(out _))
        {
            throw new YamlException("The YAML input is not valid. A document end was expected.");
        }
    }

    private static YamlException UnexpectedEnd() => new("The YAML input ended in the middle of a node.");

    private static void ConvertNode(EventSource source, Utf8JsonWriter writer, Context context)
    {
        var evt = source.Read() ?? throw UnexpectedEnd();
        ConvertEvent(evt, source, writer, context);
    }

    /// <summary>
    /// Converts a node whose first event has already been read, continuing to read nested events
    /// from <paramref name="source"/>.
    /// </summary>
    private static void ConvertEvent(ParsingEvent evt, EventSource source, Utf8JsonWriter writer, Context context)
    {
        switch (evt)
        {
            case Scalar scalar:
                WriteScalar(scalar, writer, context);
                return;

            case SequenceStart:
                context.EnterCollection(evt);
                writer.WriteStartArray();

                while (true)
                {
                    var next = source.Read() ?? throw UnexpectedEnd();
                    if (next is SequenceEnd)
                    {
                        break;
                    }

                    ConvertEvent(next, source, writer, context);
                }

                context.ExitCollection();
                writer.WriteEndArray();
                return;

            case MappingStart:
                ConvertMapping(source, writer, context, evt);
                return;

            case AnchorAlias alias:
                throw new YamlException(alias.Start, alias.End,
                    $"The alias '{alias.Value}' refers to an anchor that is not defined earlier in the document. Anchors must be defined before they are aliased.");

            default:
                throw new YamlException("The YAML input contains an event that is not supported.");
        }
    }

    private static void ConvertMapping(EventSource source, Utf8JsonWriter writer, Context context, ParsingEvent evt)
    {
        context.EnterCollection(evt);
        writer.WriteStartObject();

        var keys = context.PushMapping();
        var mergeSeen = false;

        while (true)
        {
            var keyEvt = source.Read() ?? throw UnexpectedEnd();
            if (keyEvt is MappingEnd)
            {
                break;
            }

            if (keyEvt is not Scalar key)
            {
                throw keyEvt switch
                {
                    AnchorAlias alias => new YamlException(alias.Start, alias.End,
                        $"The alias '{alias.Value}' refers to an anchor that is not defined earlier in the document. Anchors must be defined before they are aliased."),
                    SequenceStart sequenceKey => new YamlException(sequenceKey.Start, sequenceKey.End,
                        "Complex mapping keys (sequences or mappings used as keys) are not supported because JSON requires string keys."),
                    MappingStart mappingKey => new YamlException(mappingKey.Start, mappingKey.End,
                        "Complex mapping keys (sequences or mappings used as keys) are not supported because JSON requires string keys."),
                    _ => new YamlException("The YAML input contains an event that is not supported."),
                };
            }

            if (key.Value == "<<")
            {
                if (mergeSeen)
                {
                    throw new YamlException(key.Start, key.End,
                        "A mapping may contain at most one merge key ('<<').");
                }

                if (!context.AllowMergeKeys)
                {
                    throw new YamlException(key.Start, key.End,
                        $"YAML merge keys ('<<') are not supported. Set {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.AllowMergeKeys)} to true to enable them.");
                }

                mergeSeen = true;
                var valueEvents = ReadNodeEvents(source, key);
                EmitMergeValue(valueEvents, writer, context, keys, key);
                continue;
            }

            ValidateScalarLength(key, context);

            if (!context.AllowDuplicateKeys && !keys.Explicit.Add(key.Value))
            {
                throw new YamlException(key.Start, key.End,
                    $"The YAML input contains the duplicate mapping key '{key.Value}'. Enable {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.AllowDuplicateKeys)} to allow duplicates, in which case the last value wins.");
            }

            writer.WritePropertyName(key.Value);
            ConvertNode(source, writer, context);
        }

        context.PopMapping();
        context.ExitCollection();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Reads a complete node (scalar or balanced collection) into a buffer so that it can be
    /// replayed, for example as a merge key source.
    /// </summary>
    private static List<ParsingEvent> ReadNodeEvents(EventSource source, ParsingEvent position)
    {
        var events = new List<ParsingEvent>();
        var first = source.Read() ?? throw UnexpectedEnd();
        events.Add(first);

        if (first is not (SequenceStart or MappingStart))
        {
            return events;
        }

        var depth = 1;
        while (depth > 0)
        {
            var next = source.Read() ?? throw UnexpectedEnd();

            switch (next)
            {
                case SequenceStart or MappingStart:
                    depth++;
                    break;
                case SequenceEnd or MappingEnd:
                    depth--;
                    break;
            }

            events.Add(next);
        }

        return events;
    }

    /// <summary>
    /// Emits the value of a merge key ('&lt;&lt;'), which must be a mapping, an alias to a
    /// mapping, or a sequence of mappings. Sources are emitted in reverse order so that
    /// System.Text.Json's last-value-wins behavior produces the YAML specification's semantics
    /// (the containing mapping's keys win; the first source in the sequence wins among sources).
    /// </summary>
    private static void EmitMergeValue(List<ParsingEvent> valueEvents, Utf8JsonWriter writer, Context context, MappingKeys keys, Scalar key)
    {
        switch (valueEvents[0])
        {
            case MappingStart:
                EmitMergeSource(valueEvents, writer, context, keys, key);
                return;

            case SequenceStart:
            {
                var sources = SplitSequenceElements(valueEvents, key);
                for (var i = sources.Count - 1; i >= 0; i--)
                {
                    EmitMergeSource(sources[i], writer, context, keys, key);
                }

                return;
            }

            default:
                throw new YamlException(key.Start, key.End,
                    "The value of a merge key ('<<') must be a mapping, an alias to a mapping, or a sequence of mappings.");
        }
    }

    /// <summary>
    /// Splits the buffered events of a sequence into the buffered events of each of its elements.
    /// </summary>
    private static List<List<ParsingEvent>> SplitSequenceElements(List<ParsingEvent> events, Scalar position)
    {
        var elements = new List<List<ParsingEvent>>();
        var i = 1;

        while (i < events.Count)
        {
            var evt = events[i];

            if (evt is SequenceEnd)
            {
                break; // end of the outer sequence
            }

            if (evt is Scalar)
            {
                elements.Add([evt]);
                i++;
                continue;
            }

            // A nested collection: consume until it is balanced.
            var start = i;
            var depth = 0;

            do
            {
                if (i >= events.Count)
                {
                    throw new YamlException(position.Start, position.End, "The YAML input ended in the middle of a merge key sequence.");
                }

                if (events[i] is SequenceStart or MappingStart)
                {
                    depth++;
                }
                else if (events[i] is SequenceEnd or MappingEnd)
                {
                    depth--;
                }

                i++;
            }
            while (depth > 0);

            elements.Add(events[start..i]);
        }

        return elements;
    }

    /// <summary>
    /// Emits one merge source mapping into the containing mapping, skipping keys that were already
    /// emitted. The source mapping's keys join the containing mapping's emitted key set.
    /// </summary>
    private static void EmitMergeSource(List<ParsingEvent> events, Utf8JsonWriter writer, Context context, MappingKeys keys, Scalar key)
    {
        if (events.Count == 0 || events[0] is not MappingStart)
        {
            throw new YamlException(key.Start, key.End,
                "The value of a merge key ('<<') must be a mapping, an alias to a mapping, or a sequence of mappings.");
        }

        var source = new BufferedEventSource(events);
        source.Read(); // consume MappingStart

        while (true)
        {
            var keyEvt = source.Read();
            if (keyEvt is null or MappingEnd)
            {
                break;
            }

            if (keyEvt is not Scalar scalar)
            {
                throw new YamlException("The YAML input contains an event that is not supported inside a merge key source.");
            }

            var entryEvents = ReadNodeEvents(source, scalar);

            if (keys.Explicit.Contains(scalar.Value))
            {
                // An explicit key of the containing mapping wins over the merged key.
                continue;
            }

            ValidateScalarLength(scalar, context);
            writer.WritePropertyName(scalar.Value);
            ConvertNode(new BufferedEventSource(entryEvents), writer, context);
        }
    }

    private static void ValidateScalarLength(Scalar scalar, Context context)
    {
        if (context.MaxScalarLength > 0 && scalar.Value.Length > context.MaxScalarLength)
        {
            throw new YamlException(scalar.Start, scalar.End,
                $"The YAML input contains a scalar of {scalar.Value.Length} characters which exceeds the maximum scalar length of {context.MaxScalarLength}. Adjust {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.MaxScalarLength)} if longer scalars are expected.");
        }
    }

    private static void WriteScalar(Scalar scalar, Utf8JsonWriter writer, Context context)
    {
        ValidateScalarLength(scalar, context);

        var tag = GetTag(scalar.Tag);

        switch (tag)
        {
            case "":
                break;
            case StrTag:
                writer.WriteStringValue(scalar.Value);
                return;
            case NullTag:
                writer.WriteNullValue();
                return;
            case BoolTag:
            {
                if (!JsonScalarResolver.TryResolveBoolean(scalar.Value, out var b) || scalar.Style != ScalarStyle.Plain)
                {
                    throw new YamlException(scalar.Start, scalar.End,
                        $"The value '{scalar.Value}' cannot be resolved as a bool ('!!bool').");
                }

                writer.WriteBooleanValue(b);
                return;
            }
            case IntTag:
            case FloatTag:
            {
                if (scalar.Style != ScalarStyle.Plain || !JsonScalarResolver.TryParseNumber(scalar.Value, out var d, out _) ||
                    (tag == IntTag && JsonScalarResolver.IsFloatScalar(scalar.Value)))
                {
                    throw new YamlException(scalar.Start, scalar.End,
                        $"The value '{scalar.Value}' cannot be resolved as a number ('{tag}').");
                }

                writer.WriteNumberValue(d);
                return;
            }
            case BinaryTag:
            {
                if (!context.AllowBinaryTag)
                {
                    throw new YamlException(scalar.Start, scalar.End,
                        $"The YAML tag '{tag}' is not supported. Set {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.AllowBinaryTag)} to accept base64 content as a string.");
                }

                writer.WriteStringValue(scalar.Value);
                return;
            }
            case TimestampTag:
            {
                if (!context.AllowTimestampTag)
                {
                    throw new YamlException(scalar.Start, scalar.End,
                        $"The YAML tag '{tag}' is not supported. Set {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.AllowTimestampTag)} to accept timestamps as strings.");
                }

                writer.WriteStringValue(scalar.Value);
                return;
            }
            default:
            {
                if (context.UnknownTags == UnknownTagBehavior.Ignore)
                {
                    // A custom tag asserts an application specific type with no JSON mapping;
                    // resolve the scalar conservatively to its exact text as a string.
                    writer.WriteStringValue(scalar.Value);
                    return;
                }

                throw new YamlException(scalar.Start, scalar.End,
                    $"The YAML tag '{tag}' is not supported. Only the standard YAML tags (str, int, float, bool, null) and untagged scalars are supported. Set {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.UnknownTags)} to Ignore to resolve unknown tagged scalars as strings.");
            }
        }

        if (scalar.Style != ScalarStyle.Plain)
        {
            // Quoted and block scalars are always strings.
            writer.WriteStringValue(scalar.Value);
            return;
        }

        if (JsonScalarResolver.IsSpecialFloat(scalar.Value))
        {
            switch (context.SpecialFloats)
            {
                case SpecialFloatBehavior.String:
                    writer.WriteStringValue(scalar.Value);
                    return;
                case SpecialFloatBehavior.Reject:
                default:
                    throw new YamlException(scalar.Start, scalar.End,
                        $"The scalar '{scalar.Value}' is not supported because it has no JSON number representation. Quote the value ('\"{scalar.Value}\"') to treat it as a string, or set {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.SpecialFloats)} to {nameof(SpecialFloatBehavior.String)}.");
            }
        }

        if (JsonScalarResolver.IsNullScalar(scalar.Value))
        {
            writer.WriteNullValue();
            return;
        }

        if (JsonScalarResolver.TryResolveBoolean(scalar.Value, out var boolValue))
        {
            writer.WriteBooleanValue(boolValue);
            return;
        }

        var hexOrOctal = context.ScalarSchema == YamlScalarSchema.Core
            && (JsonScalarResolver.IsHexIntegerScalar(scalar.Value) || JsonScalarResolver.IsOctalIntegerScalar(scalar.Value));

        if (JsonScalarResolver.IsIntegerScalar(scalar.Value) || hexOrOctal)
        {
            WriteInteger(scalar.Value, writer, context, radixAware: hexOrOctal);
            return;
        }

        if (JsonScalarResolver.IsFloatScalar(scalar.Value))
        {
            WriteFloat(scalar.Value, writer);
            return;
        }

        writer.WriteStringValue(scalar.Value);
    }

    private static void WriteInteger(string value, Utf8JsonWriter writer, Context context, bool radixAware)
    {
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var longValue))
        {
            writer.WriteNumberValue(longValue);
            return;
        }

        // Integers larger than long.MaxValue: emit the exact value (normalized to valid JSON)
        // instead of losing precision to a floating point number.
        writer.WriteRawValue(NormalizeInteger(value, context, radixAware), skipInputValidation: false);
    }

    private static void WriteFloat(string value, Utf8JsonWriter writer)
    {
        if (JsonScalarResolver.TryParseNumber(value, out var decimalValue, out var doubleValue))
        {
            if (decimalValue != 0)
            {
                writer.WriteNumberValue(decimalValue);
            }
            else
            {
                writer.WriteNumberValue(doubleValue);
            }
        }
    }

    /// <summary>
    /// Normalizes an integer scalar that does not fit into <see cref="long"/> into a valid JSON
    /// number literal, dropping leading zeros, explicit plus signs, and hexadecimal/octal
    /// notations that are invalid in JSON.
    /// </summary>
    private static string NormalizeInteger(string value, Context context, bool radixAware)
    {
        if (radixAware)
        {
            var sign = value[0] == '-' ? -1 : 1;
            var digits = value.AsSpan(value[0] is '-' or '+' ? 1 : 0);

            if (digits.StartsWith("0x") || digits.StartsWith("0X"))
            {
                // BigInteger.Parse with NumberStyles.HexNumber interprets the digits as a two's
                // complement representation; accumulate manually to get the unsigned value.
                var result = new BigInteger(0);
                foreach (var c in digits[2..])
                {
                    result = result * 16 + (c is >= '0' and <= '9' ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10);
                }

                return (sign * result).ToString(CultureInfo.InvariantCulture);
            }

            if (digits.StartsWith("0o"))
            {
                var result = new BigInteger(0);
                foreach (var c in digits[2..])
                {
                    result = result * 8 + (c - '0');
                }

                return (sign * result).ToString(CultureInfo.InvariantCulture);
            }
        }

        var negative = value[0] == '-';
        var start = value[0] is '-' or '+' ? 1 : 0;

        while (start < value.Length - 1 && value[start] == '0')
        {
            start++;
        }

        var digitsSpan = value.AsSpan(start);

        if (!negative || digitsSpan.SequenceEqual("0"))
        {
            return digitsSpan.ToString();
        }

        return "-" + digitsSpan.ToString();
    }

    /// <summary>
    /// Determines whether the scalar carries a resolvable tag. <see cref="YamlDotNet.Core.TagName.Value"/>
    /// throws for empty and non-specific tags, which must be checked first.
    /// </summary>
    private static bool IsTagged(TagName tag) => !tag.IsEmpty && !tag.IsNonSpecific;

    /// <summary>
    /// Returns the tag text of a scalar, or an empty string when the tag is empty or non-specific.
    /// </summary>
    private static string GetTag(TagName tag) => IsTagged(tag) ? tag.Value : "";
}
