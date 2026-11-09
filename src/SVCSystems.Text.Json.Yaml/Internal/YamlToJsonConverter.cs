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
        public YamlDuplicateKeyHandling DuplicateKeyHandling { get; } = options?.DuplicateKeyHandling ?? YamlDuplicateKeyHandling.Reject;
        public bool ResolveMergeKeys { get; } = options?.ResolveMergeKeys ?? false;
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

    private sealed class BufferedEventSource(IReadOnlyList<ParsingEvent> events) : EventSource
    {
        private int _index;

        public override ParsingEvent? Read() => _index < events.Count ? events[_index++] : null;
    }

    private sealed record MappingEntry(Scalar Key, List<ParsingEvent> ValueEvents, int Order);

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
        else if (parser.Current is Scalar
                 {
                     Value.Length: 0,
                     Style: ScalarStyle.Plain,
                     IsPlainImplicit: true,
                 })
        {
            writer.WriteNullValue();
            parser.MoveNext();
            ConsumeDocumentEnd(parser);
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

        if (!context.ResolveMergeKeys && context.DuplicateKeyHandling == YamlDuplicateKeyHandling.Reject)
        {
            ConvertMappingStreaming(source, writer, context, keys);
            context.PopMapping();
            context.ExitCollection();
            writer.WriteEndObject();
            return;
        }

        List<MappingEntry> entries = [];

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

            entries.Add(new MappingEntry(key, ReadNodeEvents(source), entries.Count));
        }

        foreach (var entry in ExpandMergeEntries(entries, context))
        {
            var key = entry.Key;
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
            ConvertNode(new BufferedEventSource(entry.ValueEvents), writer, context);
        }

        context.PopMapping();
        context.ExitCollection();
        writer.WriteEndObject();
    }

    private static void ConvertMappingStreaming(EventSource source, Utf8JsonWriter writer, Context context, MappingKeys keys)
    {
        while (true)
        {
            var keyEvt = source.Read() ?? throw UnexpectedEnd();
            if (keyEvt is MappingEnd)
            {
                return;
            }

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
    }

    private static List<MappingEntry> ExpandMergeEntries(List<MappingEntry> entries, Context context)
    {
        ValidateEntryKeys(entries, context);

        if (!context.ResolveMergeKeys)
        {
            if (context.DuplicateKeyHandling == YamlDuplicateKeyHandling.LastWins)
            {
                return SelectLastDuplicateEntries(entries, context, ValidateDiscardedValue);
            }

            return entries;
        }

        ValidateUniqueEntries(entries, context);

        HashSet<KeyIdentity> explicitIdentities = [];
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!IsMergeKey(entry.Key))
            {
                explicitIdentities.Add(ResolveMappingKey(entry.Key, context).Identity);
            }
        }

        Dictionary<string, int> lastExplicitEntryByName = new(StringComparer.Ordinal);
        if (context.DuplicateKeyHandling == YamlDuplicateKeyHandling.LastWins)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (!IsMergeKey(entry.Key))
                {
                    lastExplicitEntryByName[ResolveMappingKey(entry.Key, context).PropertyName] = index;
                }
            }
        }

        List<MappingEntry> result = [];
        HashSet<KeyIdentity> mergedIdentities = [];
        var lastMergeDirective = -1;
        if (context.DuplicateKeyHandling == YamlDuplicateKeyHandling.LastWins)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                if (IsMergeKey(entries[index].Key)) lastMergeDirective = index;
            }
        }

        foreach (var entry in entries)
        {
            if (!IsMergeKey(entry.Key))
            {
                var name = ResolveMappingKey(entry.Key, context).PropertyName;
                if (context.DuplicateKeyHandling != YamlDuplicateKeyHandling.LastWins
                    || lastExplicitEntryByName[name] == entry.Order)
                {
                    result.Add(entry);
                }
                else
                {
                    ValidateDiscardedValue(entry, context);
                }

                continue;
            }

            if (context.DuplicateKeyHandling == YamlDuplicateKeyHandling.LastWins
                && entry.Order != lastMergeDirective)
            {
                continue;
            }

            foreach (var mergedEntry in ReadMergeEntries(entry, context))
            {
                var identity = ResolveMappingKey(mergedEntry.Key, context).Identity;
                if (explicitIdentities.Contains(identity) || !mergedIdentities.Add(identity))
                {
                    ValidateDiscardedValue(mergedEntry, context);
                    continue;
                }

                result.Add(mergedEntry with { Order = entry.Order });
            }
        }

        if (context.DuplicateKeyHandling == YamlDuplicateKeyHandling.LastWins)
        {
            result = SelectLastDuplicateEntries(result, context, ValidateDiscardedValue);
        }

        return result;
    }

    private static List<MappingEntry> SelectLastDuplicateEntries(
        List<MappingEntry> entries,
        Context context,
        Action<MappingEntry, Context>? validateDiscarded = null)
    {
        Dictionary<string, int> lastIndexByName = new(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            lastIndexByName[ResolveMappingKey(entries[index].Key, context).PropertyName] = index;
        }

        List<MappingEntry> result = [];
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (lastIndexByName[ResolveMappingKey(entry.Key, context).PropertyName] == index)
            {
                result.Add(entry);
            }
            else
            {
                validateDiscarded?.Invoke(entry, context);
            }
        }

        return result;
    }

    private static IEnumerable<MappingEntry> ReadMergeEntries(MappingEntry mergeEntry, Context context)
    {
        if (mergeEntry.ValueEvents.Count == 0)
        {
            throw UnrecognizedMergeKey(mergeEntry.Key);
        }

        var first = mergeEntry.ValueEvents[0];
        if (first is MappingStart)
        {
            return ExpandMergeSource(mergeEntry.ValueEvents, context);
        }

        if (first is SequenceStart sequence)
        {
            List<MappingEntry> merged = [];
            ValidateCollectionTag(sequence.Tag, SeqTag, sequence, "sequence");
            context.EnterCollection(sequence);
            try
            {
                var index = 1;
                while (index < mergeEntry.ValueEvents.Count && mergeEntry.ValueEvents[index] is not SequenceEnd)
                {
                    var start = index;
                    var value = ReadNodeEvents(mergeEntry.ValueEvents, ref index);
                    if (value[0] is not MappingStart mapping)
                    {
                        throw UnrecognizedMergeKey(mergeEntry.Key);
                    }

                    ValidateCollectionTag(mapping.Tag, MapTag, mapping, "mapping");
                    merged.AddRange(ExpandMergeSource(value, context));
                    if (index <= start)
                    {
                        throw UnrecognizedMergeKey(mergeEntry.Key);
                    }
                }
            }
            finally
            {
                context.ExitCollection();
            }

            return merged;
        }

        throw UnrecognizedMergeKey(mergeEntry.Key);
    }

    private static List<MappingEntry> ExpandMergeSource(List<ParsingEvent> mappingEvents, Context context)
    {
        var mapping = (MappingStart)mappingEvents[0];
        ValidateCollectionTag(mapping.Tag, MapTag, mapping, "mapping");
        context.EnterCollection(mapping);
        try
        {
            var sourceEntries = ReadMappingEntries(mappingEvents);
            return ExpandMergeEntries(sourceEntries, context);
        }
        finally
        {
            context.ExitCollection();
        }
    }

    private static void ValidateEntryKeys(List<MappingEntry> entries, Context context)
    {
        foreach (var entry in entries)
        {
            ValidateScalarLength(entry.Key, context);
            if (!IsMergeKey(entry.Key)) _ = ResolveMappingKey(entry.Key, context);
        }
    }

    private static void ValidateDiscardedValue(MappingEntry entry, Context context)
    {
        using Utf8JsonWriter writer = new(Stream.Null);
        ConvertNode(new BufferedEventSource(entry.ValueEvents), writer, context);
        writer.Flush();
    }

    private static void ValidateUniqueEntries(List<MappingEntry> entries, Context context)
    {
        if (context.DuplicateKeyHandling != YamlDuplicateKeyHandling.Reject)
        {
            return;
        }

        HashSet<KeyIdentity> identities = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        var sawMergeKey = false;
        foreach (var entry in entries)
        {
            if (IsMergeKey(entry.Key))
            {
                if (sawMergeKey)
                {
                    throw new YamlException(entry.Key.Start, entry.Key.End,
                        "The YAML input contains the duplicate mapping key '<<'. YAML mapping keys must be unique.");
                }

                sawMergeKey = true;
                continue;
            }

            var (identity, name) = ResolveMappingKey(entry.Key, context);
            if (!identities.Add(identity))
            {
                throw new YamlException(entry.Key.Start, entry.Key.End,
                    $"The YAML input contains the duplicate mapping key '{entry.Key.Value}'. YAML mapping keys must be unique.");
            }

            if (!names.Add(name))
            {
                throw new YamlException(entry.Key.Start, entry.Key.End,
                    $"The scalar mapping key '{entry.Key.Value}' collides with another key when projected to the JSON property name '{name}'.");
            }
        }
    }

    private static List<MappingEntry> ReadMappingEntries(IReadOnlyList<ParsingEvent> events)
    {
        List<MappingEntry> entries = [];
        var index = 1;
        while (index < events.Count && events[index] is not MappingEnd)
        {
            if (events[index++] is not Scalar key)
            {
                throw new YamlException("Complex mapping keys (sequences or mappings used as keys) are not supported because JSON requires string keys.");
            }

            var valueEvents = ReadNodeEvents(events, ref index);
            entries.Add(new MappingEntry(key, valueEvents, entries.Count));
        }

        if (index >= events.Count || events[index] is not MappingEnd)
        {
            throw UnexpectedEnd();
        }

        return entries;
    }

    private static List<ParsingEvent> ReadNodeEvents(EventSource source)
    {
        var first = source.Read() ?? throw UnexpectedEnd();
        List<ParsingEvent> events = [first];
        var nesting = first.NestingIncrease;
        while (nesting > 0)
        {
            var next = source.Read() ?? throw UnexpectedEnd();
            events.Add(next);
            nesting += next.NestingIncrease;
        }

        return events;
    }

    private static List<ParsingEvent> ReadNodeEvents(IReadOnlyList<ParsingEvent> source, ref int index)
    {
        if (index >= source.Count)
        {
            throw UnexpectedEnd();
        }

        var first = source[index++];
        List<ParsingEvent> events = [first];
        var nesting = first.NestingIncrease;
        while (nesting > 0)
        {
            if (index >= source.Count)
            {
                throw UnexpectedEnd();
            }

            var next = source[index++];
            events.Add(next);
            nesting += next.NestingIncrease;
        }

        return events;
    }

    private static bool IsMergeKey(Scalar scalar)
    {
        const string mergeTag = "tag:yaml.org,2002:merge";
        return scalar.Value == "<<"
            && scalar.Style == ScalarStyle.Plain
            && scalar.Tag != "!"
            && (!IsTagged(scalar.Tag) || GetTag(scalar.Tag) == mergeTag);
    }

    private static YamlException UnrecognizedMergeKey(Scalar key) =>
        new(key.Start, key.End, "Unrecognized merge key pattern");

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
