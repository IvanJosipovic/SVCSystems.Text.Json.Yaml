using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// An <see cref="IParser"/> decorator that resolves YAML anchors and aliases by expanding alias
/// events into the buffered content of the anchored node.
/// </summary>
/// <remarks>
/// YAML aliases can only reference nodes that appeared earlier in the document, so expansion is
/// acyclic by construction. The expansion however can be exponential ("billion laughs"), so the
/// total number of replayed events is bounded by <paramref name="maxExpansionEvents"/>.
/// <para>
/// Events flowing out of this parser have all aliases expanded; nodes carrying anchors are passed
/// through unchanged, and anchor buffers capture the expanded form so nested aliases inside an
/// anchored node are already resolved when replayed.
/// </para>
/// </remarks>
/// <param name="inner">The inner parser providing the raw event stream.</param>
/// <param name="maxExpansionEvents">The alias expansion budget, in replayed events.</param>
internal sealed class AliasExpandingParser(IParser inner, long maxExpansionEvents) : IParser
{
    private readonly Queue<ParsingEvent> _pending = new();
    private readonly Dictionary<AnchorName, List<ParsingEvent>> _anchors = new();
    private readonly List<OpenBuffer> _openBuffers = [];
    private int _depth;
    private long _replayedEvents;

    private sealed class OpenBuffer
    {
        public required AnchorName Anchor { get; init; }
        public required int StartDepth { get; init; }
        public List<ParsingEvent> Events { get; } = [];
    }

    public ParsingEvent? Current { get; private set; }

    public bool MoveNext()
    {
        var evt = ReadEvent();

        // Expand alias chains until a non-alias event is reached.
        while (evt is AnchorAlias alias)
        {
            if (!_anchors.TryGetValue(alias.Value, out var buffer))
            {
                // Unresolvable alias: pass through so the converter produces a descriptive error.
                break;
            }

            foreach (var buffered in buffer)
            {
                _pending.Enqueue(buffered);
            }

            evt = ReadEvent();
        }

        if (evt is null)
        {
            Current = null;
            return false;
        }

        Current = Record(evt);
        return true;
    }

    private ParsingEvent? ReadEvent()
    {
        if (_pending.Count > 0)
        {
            _replayedEvents++;

            if (_replayedEvents > maxExpansionEvents)
            {
                throw new YamlException(
                    $"The YAML document exceeds the alias expansion limit of {maxExpansionEvents} events. Adjust {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.MaxAliasExpansionEvents)} or reduce alias usage.");
            }

            return _pending.Dequeue();
        }

        return inner.MoveNext() ? inner.Current : null;
    }

    private ParsingEvent Record(ParsingEvent evt)
    {
        AnchorName? anchor = evt switch
        {
            Scalar s => s.Anchor.IsEmpty ? (AnchorName?)null : s.Anchor,
            SequenceStart sequence => sequence.Anchor.IsEmpty ? (AnchorName?)null : sequence.Anchor,
            MappingStart mapping => mapping.Anchor.IsEmpty ? (AnchorName?)null : mapping.Anchor,
            _ => null,
        };

        if (anchor is { } anchorName)
        {
            _openBuffers.Add(new OpenBuffer { Anchor = anchorName, StartDepth = _depth });
        }

        foreach (var buffer in _openBuffers)
        {
            buffer.Events.Add(evt);
        }

        switch (evt)
        {
            case Scalar scalar when !scalar.Anchor.IsEmpty:
                // A scalar is a single event; its buffer is complete immediately.
                Close(scalar.Anchor);
                break;
            case SequenceEnd or MappingEnd:
                _depth--;
                for (var i = _openBuffers.Count - 1; i >= 0; i--)
                {
                    if (_openBuffers[i].StartDepth == _depth)
                    {
                        Close(_openBuffers[i].Anchor);
                    }
                }

                break;
            case SequenceStart or MappingStart:
                _depth++;
                break;
            case DocumentEnd:
                // Anchors do not span documents; reset the registry so anchor names can be
                // safely reused by subsequent documents of the stream.
                _anchors.Clear();
                _openBuffers.Clear();
                break;
        }

        return evt;
    }

    private void Close(AnchorName anchor)
    {
        for (var i = _openBuffers.Count - 1; i >= 0; i--)
        {
            if (_openBuffers[i].Anchor == anchor)
            {
                var buffer = _openBuffers[i];
                _openBuffers.RemoveAt(i);
                _anchors[anchor] = buffer.Events;
                return;
            }
        }
    }
}
