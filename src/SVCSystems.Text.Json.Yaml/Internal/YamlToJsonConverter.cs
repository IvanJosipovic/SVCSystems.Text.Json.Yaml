using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using Scalar = YamlDotNet.Core.Events.Scalar;

// Portions of this file are adapted from YamlDotNet.System.Text.Json
// (https://github.com/IvanJosipovic/YamlDotNet.System.Text.Json), MIT licensed.

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>Converts YAML parsing events into JSON tokens.</summary>
internal static class YamlToJsonConverter
{
    private const string TagPrefix = "tag:yaml.org,2002:";
    private const string StrTag = TagPrefix + "str";
    private const string NullTag = TagPrefix + "null";
    private const string BoolTag = TagPrefix + "bool";
    private const string IntTag = TagPrefix + "int";
    private const string FloatTag = TagPrefix + "float";
    private const string SeqTag = TagPrefix + "seq";
    private const string MapTag = TagPrefix + "map";

    private readonly record struct KeyIdentity(string Tag, string CanonicalValue);

    private sealed class MappingKeys
    {
        public HashSet<KeyIdentity> Values { get; } = [];
        public HashSet<string> JsonNames { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Context(YamlSerializerOptions? options)
    {
        public YamlScalarSchema ScalarSchema { get; } = options?.ScalarSchema ?? YamlScalarSchema.Core;
        public int MaxDepth { get; } = options?.MaxDepth ?? YamlSerializerOptions.DefaultMaxDepth;
        public int MaxScalarLength { get; } = options?.MaxScalarLength ?? 0;
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
        public MappingKeys PushMapping() => AddMapping();
        private MappingKeys AddMapping()
        {
            var keys = new MappingKeys();
            KeyStack.Add(keys);
            return keys;
        }
        public void PopMapping() => KeyStack.RemoveAt(KeyStack.Count - 1);
    }

    private abstract class EventSource
    {
        public abstract ParsingEvent? Read();
    }

    private sealed class LiveEventSource(IParser parser) : EventSource
    {
        public override ParsingEvent? Read()
        {
            ParsingEvent? evt;
            if (parser.Current is { } current)
            {
                evt = current;
            }
            else
            {
                if (!parser.MoveNext()) return null;
                evt = parser.Current!;
            }

            parser.MoveNext();
            return evt;
        }
    }

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

    public static bool ConvertNextDocument(IParser parser, Utf8JsonWriter writer, YamlSerializerOptions? options = null)
    {
        if (parser.Current is null && !parser.MoveNext()) return false;
        parser.TryConsume<StreamStart>(out _);

        if (!parser.TryConsume<DocumentStart>(out var documentStart)) return false;
        ValidateVersion(documentStart!);

        var context = new Context(options);
        if (parser.TryConsume<DocumentEnd>(out _))
        {
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

    private static void ValidateVersion(DocumentStart documentStart)
    {
        if (documentStart.Version is { } directive
            && (directive.Version.Major != 1 || directive.Version.Minor != 2))
        {
            throw new YamlException(documentStart.Start, documentStart.End,
                $"YAML version {directive.Version.Major}.{directive.Version.Minor} is not supported; this library implements YAML 1.2.2 semantics.");
        }
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
        ConvertEvent(source.Read() ?? throw UnexpectedEnd(), source, writer, context);
    }

    private static void ConvertEvent(ParsingEvent evt, EventSource source, Utf8JsonWriter writer, Context context)
    {
        switch (evt)
        {
            case Scalar scalar:
                WriteScalar(scalar, writer, context);
                return;

            case SequenceStart sequence:
                ValidateCollectionTag(sequence.Tag, SeqTag, sequence, "sequence");
                context.EnterCollection(evt);
                writer.WriteStartArray();
                while (true)
                {
                    var next = source.Read() ?? throw UnexpectedEnd();
                    if (next is SequenceEnd) break;
                    ConvertEvent(next, source, writer, context);
                }
                context.ExitCollection();
                writer.WriteEndArray();
                return;

            case MappingStart mapping:
                ValidateCollectionTag(mapping.Tag, MapTag, mapping, "mapping");
                ConvertMapping(source, writer, context, evt);
                return;

            case AnchorAlias alias:
                throw UndefinedAlias(alias);

            default:
                throw new YamlException("The YAML input contains an event that is not supported.");
        }
    }

    private static void ConvertMapping(EventSource source, Utf8JsonWriter writer, Context context, ParsingEvent evt)
    {
        context.EnterCollection(evt);
        writer.WriteStartObject();
        var keys = context.PushMapping();

        while (true)
        {
            var keyEvt = source.Read() ?? throw UnexpectedEnd();
            if (keyEvt is MappingEnd) break;

            if (keyEvt is not Scalar key)
            {
                throw keyEvt switch
                {
                    AnchorAlias alias => UndefinedAlias(alias),
                    SequenceStart sequenceKey => new YamlException(sequenceKey.Start, sequenceKey.End,
                        "Complex mapping keys (sequences or mappings used as keys) are not supported because JSON requires string keys."),
                    MappingStart mappingKey => new YamlException(mappingKey.Start, mappingKey.End,
                        "Complex mapping keys (sequences or mappings used as keys) are not supported because JSON requires string keys."),
                    _ => new YamlException("The YAML input contains an event that is not supported as a mapping key."),
                };
            }

            ValidateScalarLength(key, context);
            var (identity, propertyName) = ResolveMappingKey(key, context);
            if (!keys.Values.Add(identity))
            {
                throw new YamlException(key.Start, key.End,
                    $"The YAML input contains the duplicate mapping key '{key.Value}'. YAML mapping keys must be unique.");
            }

            if (!keys.JsonNames.Add(propertyName))
            {
                throw new YamlException(key.Start, key.End,
                    $"The scalar mapping key '{key.Value}' collides with another key when projected to the JSON property name '{propertyName}'.");
            }

            writer.WritePropertyName(propertyName);
            ConvertNode(source, writer, context);
        }

        context.PopMapping();
        context.ExitCollection();
        writer.WriteEndObject();
    }

    private static (KeyIdentity Identity, string PropertyName) ResolveMappingKey(Scalar scalar, Context context)
    {
        var tag = GetTag(scalar.Tag);
        var kind = ResolveScalarKind(scalar, context);
        string canonical;
        string propertyName;

        switch (kind)
        {
            case JsonScalarKind.String:
                canonical = propertyName = scalar.Value;
                tag = StrTag;
                break;
            case JsonScalarKind.Null:
                canonical = propertyName = "null";
                tag = NullTag;
                break;
            case JsonScalarKind.Boolean:
                JsonScalarResolver.TryResolveTaggedBoolean(scalar.Value, context.ScalarSchema, out var booleanValue);
                canonical = propertyName = booleanValue ? "true" : "false";
                tag = BoolTag;
                break;
            case JsonScalarKind.Integer:
                canonical = propertyName = JsonScalarResolver.NormalizeInteger(scalar.Value);
                tag = IntTag;
                break;
            case JsonScalarKind.Float:
                if (JsonScalarResolver.IsSpecialFloat(scalar.Value))
                {
                    throw UnsupportedSpecialFloat(scalar);
                }
                canonical = JsonScalarResolver.CanonicalFloat(scalar.Value);
                propertyName = canonical;
                tag = FloatTag;
                break;
            default:
                throw new InvalidOperationException("Unknown YAML scalar kind.");
        }

        return (new KeyIdentity(tag, canonical), propertyName);
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
        var kind = ResolveScalarKind(scalar, context);

        switch (kind)
        {
            case JsonScalarKind.String:
                writer.WriteStringValue(scalar.Value);
                return;
            case JsonScalarKind.Null:
                writer.WriteNullValue();
                return;
            case JsonScalarKind.Boolean:
                JsonScalarResolver.TryResolveTaggedBoolean(scalar.Value, context.ScalarSchema, out var value);
                writer.WriteBooleanValue(value);
                return;
            case JsonScalarKind.Integer:
                var normalized = JsonScalarResolver.NormalizeInteger(scalar.Value);
                var jsonInteger = scalar.Value.StartsWith('-') && normalized == "0" ? "-0" : normalized;
                writer.WriteRawValue(jsonInteger, skipInputValidation: false);
                return;
            case JsonScalarKind.Float:
                if (JsonScalarResolver.IsSpecialFloat(scalar.Value)) throw UnsupportedSpecialFloat(scalar);
                writer.WriteRawValue(JsonScalarResolver.NormalizeFloat(scalar.Value), skipInputValidation: false);
                return;
            default:
                throw new InvalidOperationException("Unknown YAML scalar kind.");
        }
    }

    private static JsonScalarKind ResolveScalarKind(Scalar scalar, Context context)
    {
        // The explicit non-specific tag "!" resolves scalar nodes to strings by YAML convention.
        if (scalar.Tag == "!") return JsonScalarKind.String;

        var tag = GetTag(scalar.Tag);
        if (tag.Length == 0)
        {
            var implicitKind = JsonScalarResolver.ResolveImplicit(scalar, context.ScalarSchema);
            if (implicitKind == JsonScalarKind.Error)
            {
                throw UnresolvedJsonSchemaScalar(scalar);
            }

            if (implicitKind == JsonScalarKind.Float && JsonScalarResolver.IsSpecialFloat(scalar.Value))
            {
                throw UnsupportedSpecialFloat(scalar);
            }
            return implicitKind;
        }

        switch (tag)
        {
            case StrTag:
                return JsonScalarKind.String;
            case NullTag:
                if (JsonScalarResolver.IsNullScalar(scalar.Value, context.ScalarSchema)) return JsonScalarKind.Null;
                break;
            case BoolTag:
                if (JsonScalarResolver.TryResolveTaggedBoolean(scalar.Value, context.ScalarSchema, out _)) return JsonScalarKind.Boolean;
                break;
            case IntTag:
                if (JsonScalarResolver.IsIntegerScalar(scalar.Value, context.ScalarSchema)) return JsonScalarKind.Integer;
                break;
            case FloatTag:
                if (JsonScalarResolver.IsSpecialFloat(scalar.Value)) throw UnsupportedSpecialFloat(scalar);
                if (JsonScalarResolver.IsFloatScalar(scalar.Value, context.ScalarSchema)
                    || IsDecimalInteger(scalar.Value, context.ScalarSchema)) return JsonScalarKind.Float;
                break;
            default:
                throw new YamlException(scalar.Start, scalar.End,
                    $"The YAML tag '{tag}' is not supported. Supported tags are !!str, !!null, !!bool, !!int, !!float, !!seq, and !!map.");
        }

        throw new YamlException(scalar.Start, scalar.End,
            $"The value '{scalar.Value}' cannot be resolved using the YAML tag '{tag}'.");
    }

    private static YamlException UnresolvedJsonSchemaScalar(Scalar scalar)
    {
        return new YamlException(scalar.Start, scalar.End,
            $"The plain scalar '{scalar.Value}' does not match the YAML 1.2 JSON schema. Quote it or tag it with !!str to represent a string.");
    }

    private static bool IsDecimalInteger(string value, YamlScalarSchema schema)
    {
        if (schema == YamlScalarSchema.Json)
        {
            return JsonScalarResolver.IsIntegerScalar(value, schema);
        }

        return value.Length > 0
            && JsonScalarResolver.IsIntegerScalar(value, YamlScalarSchema.Core)
            && !value.AsSpan(value[0] is '-' or '+' ? 1 : 0).StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && !value.AsSpan(value[0] is '-' or '+' ? 1 : 0).StartsWith("0o", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateCollectionTag(TagName tagName, string expectedTag, ParsingEvent evt, string kind)
    {
        var tag = GetTag(tagName);
        if (tag.Length != 0 && tag != expectedTag)
        {
            throw new YamlException(evt.Start, evt.End,
                $"The YAML tag '{tag}' is not supported on a {kind} node. Expected '!!{(kind == "sequence" ? "seq" : "map")}' or no specific tag.");
        }
    }

    private static YamlException UnsupportedSpecialFloat(Scalar scalar)
    {
        return new YamlException(scalar.Start, scalar.End,
            $"The scalar '{scalar.Value}' is not supported because it has no JSON number representation. Quote the value ('\"{scalar.Value}\"') to treat it as a string.");
    }

    private static YamlException UndefinedAlias(AnchorAlias alias)
    {
        return new YamlException(alias.Start, alias.End,
            $"The alias '{alias.Value}' refers to an anchor that is not defined earlier in the document. Anchors must be defined before they are aliased.");
    }

    private static bool IsTagged(TagName tag) => !tag.IsEmpty && !tag.IsNonSpecific;
    private static string GetTag(TagName tag) => IsTagged(tag) ? tag.Value : "";
}
