namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// A <see cref="TextReader"/> decorator that enforces a maximum number of characters,
/// <see cref="YamlSerializerOptions.MaxInputLength"/>, so stream based deserialization cannot read
/// unbounded input.
/// </summary>
internal sealed class LengthCheckingTextReader(TextReader inner, int maxLength) : TextReader
{
    private int _read;

    private void Count(int characters)
    {
        _read += characters;

        if (maxLength > 0 && _read > maxLength)
        {
            throw new YamlDotNet.Core.YamlException(
                $"The YAML input exceeds the maximum input length of {maxLength} characters. Adjust {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.MaxInputLength)} to allow larger inputs.");
        }
    }

    public override int Peek() => inner.Peek();

    public override int Read()
    {
        var value = inner.Read();
        Count(value == -1 ? 0 : 1);
        return value;
    }

    public override int Read(char[] buffer, int index, int count)
    {
        var read = inner.Read(buffer, index, count);
        Count(read);
        return read;
    }

    public override int ReadBlock(char[] buffer, int index, int count)
    {
        var read = inner.ReadBlock(buffer, index, count);
        Count(read);
        return read;
    }

    public override string? ReadLine()
    {
        var line = inner.ReadLine();
        Count(line is null ? 0 : line.Length + 1);
        return line;
    }

    public override string ReadToEnd()
    {
        var text = inner.ReadToEnd();
        Count(text.Length);
        return text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
