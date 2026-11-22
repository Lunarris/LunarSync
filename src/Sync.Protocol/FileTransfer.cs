using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Sync.Protocol;

public sealed record UploadResult(string? Error);

public static class FileError
{
    public const string NotRequested = "not_requested";

    public const string HashMismatch = "hash_mismatch";

    public const string TooLarge = "too_large";

    public const string NotAvailable = "not_available";

    public const string InvalidHash = "invalid_hash";

    public const string NotAllowed = "not_allowed";
}

public static class FileContent
{
    public const int HeadBytes = 4;

    public static bool IsBlocked(ReadOnlySpan<byte> head) =>
        head.StartsWith("MZ"u8) || head.StartsWith("\u007FELF"u8);
}

public static class FileTransferLimits
{
    public const long MaxFileBytes = 512L * 1024 * 1024;

    public const long MaxCompressedBytes = MaxFileBytes + 4 * 1024 * 1024;

    public const int ChunkBytes = 128 * 1024;

    public const int MaxHashesPerQuery = CharacterDataLimits.MaxFileEntries;

    public static bool IsHash(string? hash) => CharacterDataLimits.IsHash(hash);
}

public static class FileCodec
{
    private const int Quality = 4;
    private const int Window = 22;

    public static async IAsyncEnumerable<byte[]> EncodeFileAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await foreach (var chunk in EncodeAsync(file, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    public static async IAsyncEnumerable<byte[]> EncodeAsync(Stream input, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var encoder = new ChunkEncoder();
        var buffer = ArrayPool<byte>.Shared.Rent(FileTransferLimits.ChunkBytes);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, FileTransferLimits.ChunkBytes), cancellationToken).ConfigureAwait(false);
                foreach (var chunk in encoder.Push(buffer.AsSpan(0, read), read == 0))
                    yield return chunk;
                if (read == 0)
                    yield break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class ChunkEncoder : IDisposable
    {
        private BrotliEncoder _encoder = new(Quality, Window);
        private readonly byte[] _output = new byte[FileTransferLimits.ChunkBytes];
        private int _filled;

        public List<byte[]> Push(ReadOnlySpan<byte> input, bool final)
        {
            var ready = new List<byte[]>();
            while (true)
            {
                var status = _encoder.Compress(input, _output.AsSpan(_filled), out var consumed, out var written, final);
                input = input[consumed..];
                _filled += written;
                if (_filled == _output.Length)
                    Flush(ready);
                if (status == OperationStatus.InvalidData)
                    throw new InvalidDataException("Brotli encoder failed.");
                if (status == OperationStatus.Done || (status == OperationStatus.NeedMoreData && input.IsEmpty))
                    break;
            }
            if (final && _filled > 0)
                Flush(ready);
            return ready;
        }

        private void Flush(List<byte[]> ready)
        {
            ready.Add(_output.AsSpan(0, _filled).ToArray());
            _filled = 0;
        }

        public void Dispose() => _encoder.Dispose();
    }
}

public sealed class ChunkDecoder : IDisposable
{
    private BrotliDecoder _decoder;
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _buffer = new byte[FileTransferLimits.ChunkBytes];
    private readonly Stream? _output;
    private readonly long _maxBytes;
    private readonly byte[] _head = new byte[FileContent.HeadBytes];
    private int _headLength;

    public ChunkDecoder(Stream? output = null, long maxBytes = FileTransferLimits.MaxFileBytes)
    {
        _decoder = new BrotliDecoder();
        _output = output;
        _maxBytes = maxBytes;
    }

    public long Size { get; private set; }

    public bool Blocked { get; private set; }
    public bool Completed { get; private set; }

    public async ValueTask WriteAsync(byte[] chunk, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        while (true)
        {
            if (Completed)
            {
                if (offset < chunk.Length)
                    throw new InvalidDataException("Data after the end of the stream.");
                return;
            }
            var (status, consumed, written) = Step(chunk.AsSpan(offset));
            offset += consumed;
            if (written > 0)
            {
                Size += written;
                if (Size > _maxBytes)
                    throw new InvalidDataException("File is larger than allowed.");
                CheckHead(written);
                _sha.AppendData(_buffer, 0, written);
                if (_output is not null)
                    await _output.WriteAsync(_buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }
            switch (status)
            {
                case OperationStatus.Done:
                    Completed = true;
                    break;
                case OperationStatus.InvalidData:
                    throw new InvalidDataException("Corrupt compressed data.");
                case OperationStatus.NeedMoreData when offset >= chunk.Length || (consumed == 0 && written == 0):
                    return;
            }
        }
    }

    private void CheckHead(int written)
    {
        if (_headLength >= _head.Length)
            return;
        var take = Math.Min(written, _head.Length - _headLength);
        _buffer.AsSpan(0, take).CopyTo(_head.AsSpan(_headLength));
        _headLength += take;
        if (FileContent.IsBlocked(_head.AsSpan(0, _headLength)))
        {
            Blocked = true;
            throw new InvalidDataException("Executable content is not allowed.");
        }
    }

    private (OperationStatus, int, int) Step(ReadOnlySpan<byte> input)
    {
        var status = _decoder.Decompress(input, _buffer, out var consumed, out var written);
        return (status, consumed, written);
    }

    public string Finish()
    {
        if (!Completed)
            throw new InvalidDataException("Compressed stream ended early.");
        return Convert.ToHexStringLower(_sha.GetHashAndReset());
    }

    public void Dispose()
    {
        _decoder.Dispose();
        _sha.Dispose();
    }
}
