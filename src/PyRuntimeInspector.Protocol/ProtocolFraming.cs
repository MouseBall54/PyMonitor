using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyRuntimeInspector.Protocol;

public sealed record ProtocolFrame(JsonObject Header, byte[] Binary);

public static class ProtocolFraming
{
    public const int MaxHeaderBytes = 1024 * 1024;
    public const int MaxBinaryBytes = 8 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, JsonObject header, ReadOnlyMemory<byte> binary = default, CancellationToken cancellationToken = default)
    {
        if (binary.Length > MaxBinaryBytes)
            throw new ProtocolException("Binary payload is too large.");

        var outgoing = (JsonObject)header.DeepClone();
        outgoing["binaryLength"] = binary.Length;
        var encoded = JsonSerializer.SerializeToUtf8Bytes(outgoing);
        if (encoded.Length == 0 || encoded.Length > MaxHeaderBytes)
            throw new ProtocolException("JSON header length is invalid.");

        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)encoded.Length));
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(encoded, cancellationToken);
        if (!binary.IsEmpty)
            await stream.WriteAsync(binary, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<ProtocolFrame> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (headerLength == 0 || headerLength > MaxHeaderBytes)
            throw new ProtocolException("JSON header length is invalid.");

        var encoded = new byte[headerLength];
        await stream.ReadExactlyAsync(encoded, cancellationToken);
        JsonObject header;
        try
        {
            header = JsonNode.Parse(Encoding.UTF8.GetString(encoded)) as JsonObject
                ?? throw new ProtocolException("JSON header must be an object.");
        }
        catch (JsonException exception)
        {
            throw new ProtocolException($"JSON header is invalid: {exception.Message}");
        }

        var binaryLength = header["binaryLength"]?.GetValue<int>() ?? 0;
        if (binaryLength < 0 || binaryLength > MaxBinaryBytes)
            throw new ProtocolException("Binary payload length is invalid.");
        var binary = new byte[binaryLength];
        if (binaryLength > 0)
            await stream.ReadExactlyAsync(binary, cancellationToken);
        return new ProtocolFrame(header, binary);
    }
}
