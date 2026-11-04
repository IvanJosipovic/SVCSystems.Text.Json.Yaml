using System.Buffers;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

// Portions of this file are adapted from YamlDotNet.System.Text.Json
// (https://github.com/IvanJosipovic/YamlDotNet.System.Text.Json), MIT licensed.

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// Converts a JSON token stream into YAML events written to a YamlDotNet emitter.
/// </summary>
/// <remarks>
/// Property order, numeric precision and string values are preserved. Strings that a YAML parser
/// could misinterpret as numbers, booleans or null are quoted so that a subsequent YAML parse
/// produces the original JSON value. The quoting decision is schema aware: under the core schema,
/// hexadecimal and octal forms also resolve to numbers and are therefore quoted.
/// </remarks>
internal static class JsonToYamlConverter
{
    internal static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Emitter configuration captured once per conversion.</summary>
    private sealed class EmitterContext
    {
        public EmitterContext(YamlSerializerOptions? options)
        {
            MappingStyle = (options?.UseFlowStyle ?? false) ? MappingStyle.Flow : MappingStyle.Block;
            SequenceStyle = (options?.UseFlowStyle ?? false) ? SequenceStyle.Flow : SequenceStyle.Block;
            Schema = options?.ScalarSchema ?? YamlScalarSchema.Core;
            StringStyle = options?.StringStyle ?? YamlStringStyle.Auto;
        }

        public MappingStyle MappingStyle { get; }
        public SequenceStyle SequenceStyle { get; }
        public YamlScalarSchema Schema { get; }
        public YamlStringStyle StringStyle { get; }
    }

    public static string ConvertToString(ReadOnlySpan<byte> utf8Json, YamlSerializerOptions? options = null)
    {
        var writer = new StringWriter();
        Convert(utf8Json, writer, options);
        return writer.ToString();
    }

    public static void Convert(ReadOnlySpan<byte> utf8Json, TextWriter output, YamlSerializerOptions? options = null)
    {
        var context = new EmitterContext(options);
        var emitter = new Emitter(output, CreateEmitterSettings(options));

        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, options is null || !options.EmitDocumentStart, Mark.Empty, Mark.Empty));

        var reader = new Utf8JsonReader(utf8Json);

        if (!reader.Read())
        {
            throw new InvalidOperationException("The JSON payload produced by System.Text.Json is empty.");
        }

        ConvertNode(ref reader, emitter, context);

        emitter.Emit(new DocumentEnd(true, Mark.Empty, Mark.Empty));
        emitter.Emit(new StreamEnd());
    }

    internal static EmitterSettings CreateEmitterSettings(YamlSerializerOptions? options)
    {
        var settings = EmitterSettings.Default.WithBestIndent(options?.Indentation ?? YamlSerializerOptions.DefaultIndentation);

        if (options is null)
        {
            return settings;
        }

        settings = settings
            .WithBestWidth(options.Width)
            .WithNewLine(options.NewLine)
            .WithMaxSimpleKeyLength(options.MaxSimpleKeyLength);

        return options.IndentedSequences ? settings.WithIndentedSequences() : settings;
    }

    private static void ConvertNode(ref Utf8JsonReader reader, IEmitter emitter, EmitterContext context)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                emitter.Emit(new MappingStart(default, default, true, context.MappingStyle));

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        break;
                    }

                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        throw new InvalidOperationException($"Unexpected JSON token '{reader.TokenType}' inside an object.");
                    }

                    var name = reader.GetString()!;
                    WriteKeyScalar(emitter, name, context);

                    reader.Read();
                    ConvertNode(ref reader, emitter, context);
                }

                emitter.Emit(new MappingEnd());
                break;
            }
            case JsonTokenType.StartArray:
            {
                emitter.Emit(new SequenceStart(default, default, true, context.SequenceStyle));

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        break;
                    }

                    ConvertNode(ref reader, emitter, context);
                }

                emitter.Emit(new SequenceEnd());
                break;
            }
            case JsonTokenType.String:
                WriteScalar(emitter, reader.GetString()!, context);
                break;
            case JsonTokenType.Number:
                // Numbers keep their exact textual representation and are emitted as plain scalars.
                emitter.Emit(new Scalar(GetRawText(ref reader)));
                break;
            case JsonTokenType.True:
                emitter.Emit(new Scalar("true"));
                break;
            case JsonTokenType.False:
                emitter.Emit(new Scalar("false"));
                break;
            case JsonTokenType.Null:
                emitter.Emit(new Scalar("null"));
                break;
            default:
                throw new InvalidOperationException($"Unexpected JSON token '{reader.TokenType}'.");
        }
    }

    /// <summary>
    /// Writes a mapping key. Simple keys must be single line, so multi line keys are always double
    /// quoted instead of using a block style, and the <see cref="YamlStringStyle.Literal"/>
    /// preference does not apply to keys.
    /// </summary>
    private static void WriteKeyScalar(IEmitter emitter, string value, EmitterContext context)
    {
        if (value.Contains('\n') || context.StringStyle == YamlStringStyle.DoubleQuoted)
        {
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.DoubleQuoted, false, true));
        }
        else if (JsonScalarResolver.NeedsQuoting(value, context.Schema))
        {
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.SingleQuoted, false, true));
        }
        else
        {
            emitter.Emit(new Scalar(value));
        }
    }

    private static void WriteScalar(IEmitter emitter, string value, EmitterContext context)
    {
        if (value.Contains('\n') && context.StringStyle != YamlStringStyle.DoubleQuoted)
        {
            // Force a multi line literal block so embedded newlines round-trip exactly.
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.Literal, true, true));
        }
        else if (context.StringStyle == YamlStringStyle.DoubleQuoted)
        {
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.DoubleQuoted, false, true));
        }
        else if (context.StringStyle == YamlStringStyle.Literal)
        {
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.Literal, true, true));
        }
        else if (JsonScalarResolver.NeedsQuoting(value, context.Schema))
        {
            // The value could be misread as null, a boolean or a number; quote it to preserve the string.
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.SingleQuoted, false, true));
        }
        else
        {
            emitter.Emit(new Scalar(value));
        }
    }

    private static string GetRawText(ref Utf8JsonReader reader)
    {
        if (reader.HasValueSequence)
        {
            var sequence = reader.ValueSequence;
            var buffer = ArrayPool<byte>.Shared.Rent((int)sequence.Length);

            try
            {
                sequence.CopyTo(buffer);
                return Encoding.UTF8.GetString(buffer, 0, (int)sequence.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return Encoding.UTF8.GetString(reader.ValueSpan.ToArray());
    }
}
