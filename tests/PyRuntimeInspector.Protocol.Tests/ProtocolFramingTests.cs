using System.Buffers.Binary;
using System.Text.Json.Nodes;
using PyRuntimeInspector.Protocol;
using Xunit;

namespace PyRuntimeInspector.Protocol.Tests;

public sealed class ProtocolFramingTests
{
    [Fact]
    public async Task RoundTripPreservesHeaderAndBinary()
    {
        await using var stream = new MemoryStream();
        await ProtocolFraming.WriteAsync(stream, new JsonObject { ["requestId"] = "one" }, new byte[] { 1, 2, 3 });
        stream.Position = 0;
        var frame = await ProtocolFraming.ReadAsync(stream);
        Assert.Equal("one", frame.Header["requestId"]!.GetValue<string>());
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Binary);
        Assert.Equal(3, frame.Header["binaryLength"]!.GetValue<int>());
    }

    [Fact]
    public async Task OversizedHeaderIsRejectedBeforeAllocation()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, ProtocolFraming.MaxHeaderBytes + 1);
        await using var stream = new MemoryStream(prefix);
        await Assert.ThrowsAsync<ProtocolException>(() => ProtocolFraming.ReadAsync(stream));
    }
}
