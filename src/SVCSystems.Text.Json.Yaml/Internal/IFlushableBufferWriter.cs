using System.Buffers;

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>A buffer writer that can commit its currently buffered bytes to its destination.</summary>
internal interface IFlushableBufferWriter : IBufferWriter<byte>
{
    void Flush();
}
