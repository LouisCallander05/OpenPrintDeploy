using System.Buffers.Binary;
using System.Text.Json;

namespace OpenPrintDeploy.Shared.Drivers;

public static class DriverWire
{
    /// <summary>
    /// Sends one response and waits for the existing client to close after reading it.
    /// The caller must provide a bounded cancellation token before disconnecting the pipe.
    /// </summary>
    public static async Task WriteResponseAsync(Stream stream, DriverResponse response, CancellationToken ct)
    {
        await WriteAsync(stream, response, ct);
        // DisconnectNamedPipe discards unread bytes. FlushAsync on a PipeStream
        // does not wait for the reader, and WaitForPipeDrain cannot be cancelled.
        // v1 clients already dispose their connection after reading the response,
        // so waiting for EOF preserves compatibility without an unbounded wait.
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, ct) != 0)
            throw new InvalidDataException("Only one driver request is allowed per connection.");
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > DriverProtocol.MaxMessageBytes) throw new InvalidDataException("Driver message is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > DriverProtocol.MaxMessageBytes) throw new InvalidDataException("Invalid driver message size.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty driver message.");
    }

    public static async Task CopyLimitedAsync(Stream source, Stream target, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("Download exceeds its size limit.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
