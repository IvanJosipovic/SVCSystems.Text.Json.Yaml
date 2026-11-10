using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SVCSystems.Text.Json.Yaml.Internal;
using YamlDotNet.Core;

namespace SVCSystems.Text.Json.Yaml.Tests;

/// <summary>
/// Tests for the phase 7 completion plan (docs/phase-7-completion-plan.md): the
/// <see cref="SequenceTextReader"/> and <see cref="Utf8BufferTextWriter"/> adapters and the
/// public <c>IBufferWriter</c> sink and UTF-8 byte deserialization APIs.
/// </summary>
public partial class BufferApiTests
{
    public sealed class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    [JsonSerializable(typeof(Person))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(JsonElement))]
    internal sealed partial class BufferJsonContext : JsonSerializerContext;

    private static JsonTypeInfo<T> TypeInfo<T>(Func<BufferJsonContext, JsonTypeInfo<T>> selector)
    {
        return selector(BufferJsonContext.Default);
    }

    #region Utf8BufferTextWriter

    private static byte[] WrittenBytes(Action<Utf8BufferTextWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var textWriter = new Utf8BufferTextWriter(buffer))
        {
            write(textWriter);
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static TheoryData<string> TextCorpus => new()
    {
        "hello",
        "",
        "héllo — naïve",
        "hello world from the buffer writer",
        "emoji: \U0001F389 done",
        "Control \u0007 and bell",
        "Line \u0085 separator",
        "Line \u2028 and \u2029",
        "タブテスト",
    };

    [Theory]
    [MemberData(nameof(TextCorpus))]
    public void EncodesExactlyLikeUtf8(string text)
    {
        WrittenBytes(w => w.Write(text)).ShouldBe(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void EncodesSurrogatePairAcrossTwoSingleCharWrites()
    {
        WrittenBytes(w =>
        {
            w.Write('\ud83c');
            w.Write('\udf89');
        }).ShouldBe(Encoding.UTF8.GetBytes("\U0001F389"));
    }

    [Fact]
    public void EncodesSurrogatePairAcrossInternalByteBufferBoundary()
    {
        var prefix = new string('a', 4095);

        WrittenBytes(w =>
        {
            w.Write(prefix);
            w.Write('\ud83c');
            w.Write('\udf89');
        }).ShouldBe(Encoding.UTF8.GetBytes(prefix + "\U0001F389"));
    }

    [Fact]
    public void EncodesSurrogatePairSplitAcrossBulkAndSingleCharWrites()
    {
        WrittenBytes(w =>
        {
            w.Write("a\ud83c");
            w.Write('\udf89');
            w.Write("b");
        }).ShouldBe(Encoding.UTF8.GetBytes("a\U0001F389b"));
    }

    [Fact]
    public void RejectsUnpairedHighSurrogateOnFlush()
    {
        Should.Throw<EncoderFallbackException>(() => WrittenBytes(w => w.Write('\ud83c')));
    }

    [Fact]
    public void EmptySpanDoesNotBreakASurrogatePairAcrossWrites()
    {
        WrittenBytes(w =>
        {
            w.Write('\ud83c');
            w.Write(ReadOnlySpan<char>.Empty);
            w.Write('\udf89');
        }).ShouldBe(Encoding.UTF8.GetBytes("\U0001F389"));
    }

    [Fact]
    public void RejectsUnpairedLowSurrogate()
    {
        Should.Throw<EncoderFallbackException>(() => WrittenBytes(w => w.Write('\udc00')));
    }

    [Fact]
    public void RejectsTwoUnpairedHighSurrogates()
    {
        // Any high surrogate followed by another high surrogate is unpairable on both sides.
        Should.Throw<EncoderFallbackException>(() => WrittenBytes(w =>
        {
            w.Write('\ud83c');
            w.Write('\ud83d');
        }));
    }

    [Fact]
    public void RejectsUnpairedHighSurrogateFollowedByBulkText()
    {
        Should.Throw<EncoderFallbackException>(() => WrittenBytes(w =>
        {
            w.Write('\ud83c');
            w.Write("ab");
        }));
    }

    [Fact]
    public void FlushRejectsPendingSurrogate()
    {
        var buffer = new ArrayBufferWriter<byte>();

        using var textWriter = new Utf8BufferTextWriter(buffer);
        textWriter.Write('\ud83c');
        Should.Throw<EncoderFallbackException>(() => textWriter.Flush());
    }

    [Fact]
    public void MatchesStreamWriterEncodingOnMixedCorpus()
    {
        foreach (var text in new[]
                 {
                     "plain",
                     "héllo",
                     "\U0001F389\U0001F600",
                     "mixed \U0001F389 pairs",
                     "\u0007\u0085\u2028\u2029",
                     "日本語テキスト",
                 })
        {
            var buffer = new ArrayBufferWriter<byte>();

            using (var textWriter = new Utf8BufferTextWriter(buffer))
            {
                foreach (var chunk in Split(text, 2))
                {
                    textWriter.Write(chunk);
                }
            }

            buffer.WrittenSpan.ToArray().ShouldBe(Encoding.UTF8.GetBytes(text), customMessage: text);
        }
    }

    private static IEnumerable<string> Split(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }

    #endregion

    #region SequenceTextReader

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(byte[] data, long runningIndex)
        {
            Memory = data;
            RunningIndex = runningIndex;
        }

        public TestSegment Append(byte[] next)
        {
            var segment = new TestSegment(next, RunningIndex + Memory.Length);
            Next = segment;
            return segment;
        }
    }

    private static ReadOnlySequence<byte> CreateSequence(params byte[][] segments)
    {
        var first = new TestSegment(segments[0], 0);
        var last = first;

        foreach (var segment in segments[1..])
        {
            last = last.Append(segment);
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    [Fact]
    public void ReadsCharsFromSingleSegmentSequence()
    {
        var reader = new SequenceTextReader(new ReadOnlySequence<byte>(Utf8("hello")));
        var buffer = new char[10];

        reader.Read(buffer, 0, 10).ShouldBe(5);
        new string(buffer, 0, 5).ShouldBe("hello");
        reader.Read(buffer, 0, 10).ShouldBe(0);
    }

    [Fact]
    public void PeeksWithoutConsuming()
    {
        var reader = new SequenceTextReader(new ReadOnlySequence<byte>(Utf8("ab")));

        reader.Peek().ShouldBe('a');
        reader.Peek().ShouldBe('a');
        reader.Read().ShouldBe('a');
        reader.Read().ShouldBe('b');
        reader.Peek().ShouldBe(-1);
        reader.Read().ShouldBe(-1);
    }

    [Fact]
    public void DecodesMultibyteRuneSplitAcrossSegments()
    {
        // \U0001F389 encodes as F0 9F 8E 89; split the rune one byte at a time.
        var bytes = Utf8("a\U0001F389b");
        var segments = bytes.Select(b => new[] { b }).ToArray();
        var reader = new SequenceTextReader(CreateSequence(segments));
        var buffer = new char[10];

        var read = reader.Read(buffer, 0, 10);

        new string(buffer, 0, read).ShouldBe("a\U0001F389b");
    }

    [Fact]
    public void DecodesMultibyteRuneSplitAcrossTwoSegments()
    {
        var bytes = Utf8("x\U0001F389y");

        foreach (var split in new[] { 2, 3, 4, 5 })
        {
            var reader = new SequenceTextReader(CreateSequence(bytes[..split], bytes[split..]));
            var buffer = new char[10];

            var read = reader.Read(buffer, 0, 10);

            new string(buffer, 0, read).ShouldBe("x\U0001F389y", customMessage: $"split at {split}");
        }
    }

    [Theory]
    [InlineData(1022)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    public void DecodesSupplementaryRuneAcrossCharacterBufferBoundary(int prefixLength)
    {
        var text = new string('x', prefixLength) + "\U0001F389" + "tail";
        var reader = new SequenceTextReader(new ReadOnlySequence<byte>(Utf8(text)));

        reader.ReadToEnd().ShouldBe(text);
    }

    [Fact]
    public void SkipsUtf8ByteOrderMark()
    {
        var reader = new SequenceTextReader(new ReadOnlySequence<byte>([0xEF, 0xBB, 0xBF, .. Utf8("a")]));
        var buffer = new char[4];

        reader.Read(buffer, 0, 4).ShouldBe(1);
        new string(buffer, 0, 1).ShouldBe("a");
    }

    [Fact]
    public void AdvancesPastEmptySegments()
    {
        var reader = new SequenceTextReader(CreateSequence([], Utf8("a")));

        reader.ReadToEnd().ShouldBe("a");
    }

    [Fact]
    public void RejectsIncompleteTrailingRune()
    {
        // 0xE2 0x82 starts a three byte rune but is incomplete UTF-8.
        var bytes = new byte[] { 0x61, 0xE2, 0x82 };
        var reader = new SequenceTextReader(new ReadOnlySequence<byte>(bytes));
        var buffer = new char[4];

        Should.Throw<DecoderFallbackException>(() => reader.Read(buffer, 0, 4));
    }

    [Fact]
    public void RejectsIncompleteRuneStraddlingTheEndOfSegments()
    {
        var reader = new SequenceTextReader(CreateSequence([0xE2], [0x82]));
        var buffer = new char[4];

        Should.Throw<DecoderFallbackException>(() => reader.Read(buffer, 0, 4));
    }

    [Fact]
    public void ThrowsWhenCancellationTokenIsCanceled()
    {
        var reader = new SequenceTextReader(new ReadOnlySequence<byte>(Utf8("abc")), new CancellationToken(true));

        Action read = () => reader.Read();

        read.ShouldThrow<OperationCanceledException>();
    }

    #endregion

    #region Serialize IBufferWriter sinks

    [Fact]
    public void SerializeToBufferWriterMatchesUtf8OfStringOverload()
    {
        var person = new Person { Name = "John", Age = 30 };
        var expected = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(person, TypeInfo(x => x.Person)));

        var buffer = new ArrayBufferWriter<byte>();

        YamlSerializer.Serialize(buffer, person, TypeInfo(x => x.Person));

        buffer.WrittenSpan.ToArray().ShouldBe(expected);
    }

    [Fact]
    public async Task SerializeAsyncToBufferWriterMatchesSyncOverload()
    {
        var person = new Person { Name = "Jane", Age = 42 };
        var expected = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(person, TypeInfo(x => x.Person)));

        var buffer = new ArrayBufferWriter<byte>();

        await YamlSerializer.SerializeAsync(buffer, person, TypeInfo(x => x.Person));

        buffer.WrittenSpan.ToArray().ShouldBe(expected);
    }

    [Fact]
    public void SerializeToBufferWriterRoundTrips()
    {
        var person = new Person { Name = "佐藤", Age = 42 };
        var buffer = new ArrayBufferWriter<byte>();

        YamlSerializer.Serialize(buffer, person, TypeInfo(x => x.Person));

        var result = YamlSerializer.Deserialize(buffer.WrittenSpan.ToArray(), TypeInfo(x => x.Person));

        result.ShouldNotBeNull();
        result.Name.ShouldBe("佐藤");
        result.Age.ShouldBe(42);
    }

    [Fact]
    public void SerializeToBufferWriterAppendsWithoutDisturbingTheWriter()
    {
        var buffer = new ArrayBufferWriter<byte>();
        buffer.Write("prefix:"u8.ToArray());
        var person = new Person { Name = "John", Age = 30 };

        YamlSerializer.Serialize(buffer, person, TypeInfo(x => x.Person));

        Encoding.UTF8.GetString(buffer.WrittenSpan)
            .ShouldBe("prefix:" + YamlSerializer.Serialize(person, TypeInfo(x => x.Person)));
    }

    [Fact]
    public void SerializeToBufferWriterRejectsNullArguments()
    {
        var buffer = new ArrayBufferWriter<byte>();

        Should.Throw<ArgumentNullException>(() => YamlSerializer.Serialize((IBufferWriter<byte>)null!, new Person(), TypeInfo(x => x.Person)));
        Should.Throw<ArgumentNullException>(() => YamlSerializer.Serialize(buffer, new Person(), (JsonTypeInfo<Person>)null!));
    }

    [Fact]
    public void SerializeAsyncToBufferWriterThrowsWhenTokenIsCanceled()
    {
        var buffer = new ArrayBufferWriter<byte>();

        Should.Throw<OperationCanceledException>(() =>
            YamlSerializer.SerializeAsync(buffer, new Person(), TypeInfo(x => x.Person), null, new CancellationToken(true))
                .GetAwaiter().GetResult());
    }

    [Fact]
    public void SerializeToBufferWriterHandlesUnpairedSurrogateLikeUtf8()
    {
        var value = "broken \ud83c surrogate";
        var buffer = new ArrayBufferWriter<byte>();

        YamlSerializer.Serialize(buffer, value, TypeInfo(x => x.String));

        // The unpaired surrogate becomes U+FFFD on both paths; round-tripping returns the
        // replaced text.
        var result = YamlSerializer.Deserialize(buffer.WrittenSpan.ToArray(), TypeInfo(x => x.String));

        result.ShouldBe("broken \uFFFD surrogate");
    }

    #endregion

    #region Deserialize from UTF-8 bytes

    [Fact]
    public void DeserializeFromMemoryBytesMatchesStringOverload()
    {
        const string yaml = "Name: Jane\nAge: 42\n";
        var bytes = Utf8(yaml);

        var fromString = YamlSerializer.Deserialize(yaml, TypeInfo(x => x.Person));
        var fromBytes = YamlSerializer.Deserialize(bytes, TypeInfo(x => x.Person));

        fromString.ShouldNotBeNull();
        fromBytes.ShouldNotBeNull();
        fromBytes.Name.ShouldBe(fromString.Name);
        fromBytes.Age.ShouldBe(fromString.Age);
    }

    [Fact]
    public void DeserializeFromBytesAcceptsByteOrderMark()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Utf8("Name: Jane\nAge: 42\n")];

        var result = YamlSerializer.Deserialize(bytes, TypeInfo(x => x.Person));

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Jane");
    }

    [Fact]
    public void DeserializeFromUtf8StreamAcceptsByteOrderMark()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Utf8("Name: Jane\nAge: 42\n")];
        var result = YamlSerializer.Deserialize(new MemoryStream(bytes), TypeInfo(x => x.Person));

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Jane");
    }

    [Fact]
    public void DeserializeFromBytesWithoutByteOrderMarkIsUnaffected()
    {
        var result = YamlSerializer.Deserialize(Utf8("Name: Jane\nAge: 42\n"), TypeInfo(x => x.Person));

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Jane");
    }

    [Fact]
    public void DeserializeFromSegmentedSequenceMatchesStringOverload()
    {
        var bytes = Utf8("Name: Jane\nAge: 42\n");
        var sequence = CreateSequence(bytes[..3], bytes[3..7], bytes[7..]);

        var result = YamlSerializer.Deserialize(sequence, TypeInfo(x => x.Person));

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Jane");
        result.Age.ShouldBe(42);
    }

    [Fact]
    public void DeserializeFromBytesPreservesMultibyteContent()
    {
        const string yaml = "Name: 佐藤\U0001F389\nAge: 42\n";

        var result = YamlSerializer.Deserialize(Utf8(yaml), TypeInfo(x => x.Person));

        result.ShouldNotBeNull();
        result.Name.ShouldBe("佐藤\U0001F389");
    }

    [Fact]
    public void DeserializeFromBytesRejectsMultipleDocuments()
    {
        const string yaml = "---\nName: Jane\n---\nName: John\n";

        Should.Throw<YamlException>(() => YamlSerializer.Deserialize(Utf8(yaml), TypeInfo(x => x.Person)))
            .Message.ShouldContain("Multiple YAML documents");
    }

    [Fact]
    public void DeserializeFromBytesRejectsInvalidYamlLikeStringOverload()
    {
        const string yaml = "Name: [unclosed";

        Should.Throw<YamlException>(() => YamlSerializer.Deserialize(Utf8(yaml), TypeInfo(x => x.Person)));
        Should.Throw<YamlException>(() => YamlSerializer.Deserialize(yaml, TypeInfo(x => x.Person)));
    }

    [Fact]
    public void DeserializationRejectsMalformedUtf8AcrossPublicInputForms()
    {
        var invalid = new byte[] { 0xFF };
        var typeInfo = TypeInfo(x => x.String);

        Should.Throw<DecoderFallbackException>(() => YamlSerializer.Deserialize(invalid.AsMemory(), typeInfo));
        Should.Throw<DecoderFallbackException>(() => YamlSerializer.Deserialize(new ReadOnlySequence<byte>(invalid), typeInfo));
        Should.Throw<DecoderFallbackException>(() => YamlSerializer.Deserialize(new MemoryStream(invalid), typeInfo));
    }

    [Fact]
    public async Task DeserializeAsyncFromMemoryBytesRoundTrips()
    {
        var result = await YamlSerializer.DeserializeAsync(Utf8("Name: Jane\nAge: 42\n"), TypeInfo(x => x.Person));

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Jane");
        result.Age.ShouldBe(42);
    }

    [Fact]
    public async Task DeserializeAsyncFromSequenceBytesRoundTrips()
    {
        var bytes = Utf8("Name: Jane\nAge: 42\n");
        var sequence = CreateSequence(bytes[..2], bytes[2..]);

        var result = await YamlSerializer.DeserializeAsync(sequence, TypeInfo(x => x.Person));

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Jane");
    }

    [Fact]
    public async Task AsyncDeserializationRejectsMalformedUtf8()
    {
        var invalid = new byte[] { 0xFF };
        var typeInfo = TypeInfo(x => x.String);

        await Should.ThrowAsync<DecoderFallbackException>(() =>
            YamlSerializer.DeserializeAsync(new ReadOnlyMemory<byte>(invalid), typeInfo));
        await Should.ThrowAsync<DecoderFallbackException>(() =>
            YamlSerializer.DeserializeAsync(new MemoryStream(invalid), typeInfo));
    }

    [Fact]
    public void DeserializeAsyncFromBytesThrowsWhenTokenIsCanceled()
    {
        Should.Throw<OperationCanceledException>(() =>
            YamlSerializer.DeserializeAsync(Utf8("Name: Jane\n"), TypeInfo(x => x.Person), null, new CancellationToken(true))
                .GetAwaiter().GetResult());
    }

    #endregion

    #region DeserializeAsync(Stream) re-route

    [Fact]
    public async Task DeserializeAsyncFromStreamMatchesStringOverload()
    {
        const string yaml = "Name: Jane\nAge: 42\n";
        var fromString = YamlSerializer.Deserialize(yaml, TypeInfo(x => x.Person));

        using var stream = new MemoryStream(Utf8(yaml));

        var fromStream = await YamlSerializer.DeserializeAsync(stream, TypeInfo(x => x.Person));

        fromStream.ShouldNotBeNull();
        fromStream.Name.ShouldBe(fromString!.Name);
        fromStream.Age.ShouldBe(fromString.Age);
    }

    [Fact]
    public async Task DeserializeAsyncFromStreamAcceptsByteOrderMark()
    {
        using var stream = new MemoryStream([0xEF, 0xBB, 0xBF, .. Utf8("Name: Jane\nAge: 42\n")]);

        var result = await YamlSerializer.DeserializeAsync(stream, TypeInfo(x => x.Person));

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Jane");
    }

    [Fact]
    public async Task DeserializeAsyncFromStreamThrowsWhenTokenIsCanceled()
    {
        using var stream = new MemoryStream(Utf8("Name: Jane\nAge: 42\n"));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            YamlSerializer.DeserializeAsync(stream, TypeInfo(x => x.Person), null, new CancellationToken(true)));
    }

    [Fact]
    public async Task DeserializeAsyncFromStreamPreservesYamlErrorsAcrossThePipe()
    {
        // The first document spills the converted JSON to the bounded pipe before the parser
        // encounters the second document and reports the YAML error.
        var largeName = new string('x', 160 * 1024);
        using var stream = new MemoryStream(Utf8($"Name: {largeName}\n---\nName: John\n"));

        var exception = await Should.ThrowAsync<YamlException>(async () =>
            await YamlSerializer.DeserializeAsync(stream, TypeInfo(x => x.Person)));

        exception.Message.ShouldContain("Multiple YAML documents");
    }

    [Fact]
    public async Task DeserializeAsyncFromStreamHandlesLargeScalar()
    {
        // A single ~128 KB block scalar document.
        var lines = Enumerable.Repeat(new string('x', 1024), 128).ToArray();
        var expected = string.Join("\n", lines);
        var yaml = "|-\n" + string.Join("\n", lines.Select(l => "  " + l)) + "\n";

        string? result;
        using (var stream = new MemoryStream(Utf8(yaml)))
        {
            result = await YamlSerializer.DeserializeAsync(stream, TypeInfo(x => x.String));
        }

        result.ShouldBe(expected);
    }

    #endregion

    #region Options-flavored overloads (System.Text.Json parity)

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task OptionsOverloadsRoundTripThroughStream()
    {
        ReflectionOnlyTests.SkipWhenReflectionUnavailable();

        var person = new Person { Name = "John", Age = 30 };

        var expected = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(person, CamelCase));

        using var output = new MemoryStream();
        YamlSerializer.Serialize(output, person, CamelCase);
        output.Position = 0;
        output.ToArray().ShouldBe(expected);

        using var input = new MemoryStream(expected);
        var fromStream = YamlSerializer.Deserialize<Person>(input, CamelCase);
        fromStream.ShouldNotBeNull();
        fromStream.Name.ShouldBe("John");
        fromStream.Age.ShouldBe(30);

        using var input2 = new MemoryStream(expected);
        var fromAsync = await YamlSerializer.DeserializeAsync<Person>(input2, CamelCase);
        fromAsync.ShouldNotBeNull();
        fromAsync.Age.ShouldBe(30);
    }

    [Fact]
    public async Task OptionsAsyncSerializeRoundTripsThroughStream()
    {
        ReflectionOnlyTests.SkipWhenReflectionUnavailable();

        var person = new Person { Name = "Jane", Age = 42 };

        using var output = new MemoryStream();
        await YamlSerializer.SerializeAsync(output, person, CamelCase);

        var expected = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(person, CamelCase));
        output.ToArray().ShouldBe(expected);

        output.Position = 0;
        var back = await YamlSerializer.DeserializeAsync<Person>(output, CamelCase);
        back.ShouldNotBeNull();
        back.Name.ShouldBe("Jane");
        back.Age.ShouldBe(42);
    }

    [Fact]
    public void OptionsOverloadsRoundTripThroughBytes()
    {
        ReflectionOnlyTests.SkipWhenReflectionUnavailable();

        var bytes = YamlSerializer.SerializeToUtf8Bytes(new Person { Name = "John", Age = 30 }, CamelCase);

        var fromMemory = YamlSerializer.Deserialize<Person>(bytes, CamelCase);
        fromMemory.ShouldNotBeNull();
        fromMemory.Name.ShouldBe("John");
        fromMemory.Age.ShouldBe(30);
    }

    [Fact]
    public void SerializeToUtf8BytesMatchesStringOverload()
    {
        ReflectionOnlyTests.SkipWhenReflectionUnavailable();

        var person = new Person { Name = "John", Age = 30 };
        var expected = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(person, CamelCase));

        YamlSerializer.SerializeToUtf8Bytes(person, CamelCase).ShouldBe(expected);
    }

    [Fact]
    public void SerializeToUtf8BytesWithJsonTypeInfoMatchesStringOverload()
    {
        var person = new Person { Name = "John", Age = 30 };
        var expected = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(person, BufferJsonContext.Default.Person));

        YamlSerializer.SerializeToUtf8Bytes(person, BufferJsonContext.Default.Person).ShouldBe(
            expected);
    }

    [Fact]
    public void NullRoundTripsLikeSystemTextJson()
    {
        // STJ: Serialize(null) produces "null"; Deserialize("null") produces the default value.
        YamlSerializer.Serialize((Person?)null, BufferJsonContext.Default.Person).ShouldBe("null" + Environment.NewLine);
        YamlSerializer.Deserialize<Person>("null", BufferJsonContext.Default.Person).ShouldBeNull();
        YamlSerializer.Deserialize(Utf8("null"), BufferJsonContext.Default.Person).ShouldBeNull();
    }

    [Fact]
    public void StreamsAreLeftOpenLikeSystemTextJson()
    {
        var person = new Person { Name = "John", Age = 30 };

        using var stream = new MemoryStream();
        YamlSerializer.Serialize(stream, person, BufferJsonContext.Default.Person);
        stream.CanWrite.ShouldBeTrue();

        stream.Position = 0;
        YamlSerializer.Deserialize(stream, BufferJsonContext.Default.Person);
        stream.CanRead.ShouldBeTrue();
        stream.Position.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void AsyncOptionsOverloadsHonorCancellation()
    {
        ReflectionOnlyTests.SkipWhenReflectionUnavailable();

        using var output = new MemoryStream();
        Should.Throw<OperationCanceledException>(() =>
            YamlSerializer.SerializeAsync(output, new Person(), CamelCase, null, new CancellationToken(true)).GetAwaiter().GetResult());

        using var input = new MemoryStream(Utf8("Name: Jane\nAge: 42\n"));
        Should.Throw<OperationCanceledException>(() =>
            YamlSerializer.DeserializeAsync<Person>(input, CamelCase, null, new CancellationToken(true)).GetAwaiter().GetResult());
    }

    #endregion

    #region Path equivalence corpus

    public static TheoryData<string> YamlCorpus => new()
    {
        "Name: Jane\nAge: 42\n",
        "a: 1\nb: '2'\nc: \"3\"\n",
        "list:\n- 1\n- 2\n- three\n",
        "map:\n  nested: {x: 1, y: 2}\n",
        "# comment\nkey: value # trailing\n",
        "---\nName: Jane\n...\n",
        "text: |\n  block\n  scalar\n",
        "folded: >\n  folded\n  scalar\n",
        "plain: value with spaces\n",
        "empty: ''\n",
        "anchor: &a 1\nalias: *a\n",
        "merged: &base\n  x: 1\n  y: 2\nuser:\n  <<: *base\n  z: 3\n",
        "emoji: \U0001F389\n",
        "unicode: 日本語\n",
        "multi: 'line one'\n",
        "single: \"double quoted\"\n",
    };

    [Theory]
    [MemberData(nameof(YamlCorpus))]
    public async Task ByteAndStreamAndStringPathsAgree(string yaml)
    {
        var expected = YamlSerializer.Deserialize(yaml, TypeInfo(x => x.JsonElement));
        var expectedJson = JsonSerializer.Serialize(expected, BufferJsonContext.Default.JsonElement);

        var fromBytes = YamlSerializer.Deserialize(Utf8(yaml), TypeInfo(x => x.JsonElement));
        var fromBytesJson = JsonSerializer.Serialize(fromBytes, BufferJsonContext.Default.JsonElement);
        fromBytesJson.ShouldBe(expectedJson, customMessage: yaml);

        using var stream = new MemoryStream(Utf8(yaml));
        var fromStream = await YamlSerializer.DeserializeAsync(stream, TypeInfo(x => x.JsonElement));
        var fromStreamJson = JsonSerializer.Serialize(fromStream, BufferJsonContext.Default.JsonElement);
        fromStreamJson.ShouldBe(expectedJson, customMessage: yaml);
    }

    [Theory]
    [MemberData(nameof(YamlCorpus))]
    public void SinkOutputMatchesStringOutput(string yaml)
    {
        var expected = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(yaml, TypeInfo(x => x.String)));

        var buffer = new ArrayBufferWriter<byte>();

        YamlSerializer.Serialize(buffer, yaml, TypeInfo(x => x.String));

        buffer.WrittenSpan.ToArray().ShouldBe(expected);
    }

    #endregion
}
