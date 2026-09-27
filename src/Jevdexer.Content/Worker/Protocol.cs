using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jevdexer.Content.Analysis;
using Jevdexer.Content.Models;

namespace Jevdexer.Content.Worker;

/// <summary>
/// The app and the sandboxed worker talk over the worker's stdin and stdout, one frame at a time:
/// [int32 header length][UTF-8 JSON header][int32 payload length][payload bytes]. File bytes and
/// model bytes travel as payloads: the worker cannot open files itself.
/// </summary>
public static class Protocol
{
    public const int MaxHeader = 1 << 20;
    public const int MaxPayload = 512 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write<T>(Stream stream, T header, ReadOnlySpan<byte> payload = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, json.Length);
        stream.Write(len);
        stream.Write(json);
        BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
        stream.Write(len);
        stream.Write(payload);
        stream.Flush();
    }

    /// <summary>Null at end of stream.</summary>
    public static (T Header, byte[] Payload)? Read<T>(Stream stream)
    {
        var headerLength = ReadInt(stream);
        if (headerLength is null) return null;
        if (headerLength is < 0 or > MaxHeader) throw new InvalidDataException("bad header length");
        var header = JsonSerializer.Deserialize<T>(ReadExactly(stream, headerLength.Value), Json) ?? throw new InvalidDataException("empty header");
        var payloadLength = ReadInt(stream) ?? throw new EndOfStreamException();
        if (payloadLength is < 0 or > MaxPayload) throw new InvalidDataException("bad payload length");
        return (header, ReadExactly(stream, payloadLength));
    }

    private static int? ReadInt(Stream stream)
    {
        var b = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = stream.Read(b, read, 4 - read);
            if (n == 0) return read == 0 ? null : throw new EndOfStreamException();
            read += n;
        }
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    private static byte[] ReadExactly(Stream stream, int length)
    {
        var b = new byte[length];
        stream.ReadExactly(b);
        return b;
    }
}

/// <summary>A request to the worker. Ops: init, document, photo, benchmark, exit.</summary>
/// <param name="Parts">init: payload layout, name to byte length, in order (e5, e5tok, clip). Absent parts: Light mode.</param>
/// <param name="Name">document: the file name without extension (its words describe it; the worker still cannot open the file).</param>
public sealed record WorkerRequest(string Op, string? Extension = null, Dictionary<string, int>? Parts = null, string? Name = null);

/// <summary>One stored chunk of a document: its first words (shown as the match) and its meaning vector.</summary>
public sealed record Chunk(string Head, byte[]? Vector);

/// <summary>What the worker learned about one file. Raw text never comes back, only derived data.</summary>
public sealed record ContentResult
{
    public string Kind { get; init; } = "";
    public string? Title { get; init; }
    public int Pages { get; init; }
    public bool UsedOcr { get; init; }
    public int TextLength { get; init; }
    public List<Keyword> Keywords { get; init; } = [];
    public List<Chunk> Chunks { get; init; } = [];
    public List<PhotoTag> Tags { get; init; } = [];
    public byte[]? PhotoVector { get; init; }
    public DateTime? TakenAt { get; init; }
    public List<string> Sensitive { get; init; } = [];
    /// <summary>File names the text mentions (<see cref="Analysis.References"/>): links, not content.</summary>
    public List<string> References { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>Milliseconds per unit of work on this PC, one core: decides whether Full mode is comfortable.</summary>
public sealed record BenchmarkResult(double PassageMs, double PhotoMs, bool TextModel, bool PhotoModel, string? Error = null);
