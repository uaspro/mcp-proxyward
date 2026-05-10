namespace ProxyWard.Api.Middleware;

internal sealed class ResponseInspectionStream(
    Stream passthrough,
    HttpResponse response,
    int maxBodyBytes,
    bool blockUnsupported) : Stream
{
    private readonly MemoryStream _buffer = new(capacity: Math.Min(maxBodyBytes, 64 * 1024));
    private CaptureMode _mode = CaptureMode.Undecided;

    public string? UnsupportedReason { get; private set; }

    public long ObservedBytes { get; private set; }

    public bool WasPassedThrough => _mode == CaptureMode.PassThrough;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public byte[] GetBufferedBody() => _buffer.ToArray();

    public async Task CopyBufferedBodyToAsync(Stream destination, CancellationToken cancellationToken)
    {
        _buffer.Position = 0;
        await _buffer.CopyToAsync(destination, cancellationToken);
    }

    public override void Flush()
    {
        if (_mode == CaptureMode.PassThrough)
        {
            passthrough.Flush();
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _mode == CaptureMode.PassThrough
            ? passthrough.FlushAsync(cancellationToken)
            : Task.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        DecideIfNeeded();
        ObservedBytes += buffer.Length;

        switch (_mode)
        {
            case CaptureMode.PassThrough:
                await passthrough.WriteAsync(buffer, cancellationToken);
                break;
            case CaptureMode.Buffer:
                await BufferOrPassThroughAsync(buffer, cancellationToken);
                break;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    private async ValueTask BufferOrPassThroughAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (_buffer.Length + buffer.Length <= maxBodyBytes)
        {
            await _buffer.WriteAsync(buffer, cancellationToken);
            return;
        }

        UnsupportedReason = "response_too_large";
        if (blockUnsupported)
        {
            _buffer.SetLength(0);
            _mode = CaptureMode.Discard;
            return;
        }

        _buffer.Position = 0;
        await _buffer.CopyToAsync(passthrough, cancellationToken);
        _buffer.SetLength(0);
        _mode = CaptureMode.PassThrough;
        await passthrough.WriteAsync(buffer, cancellationToken);
    }

    private void DecideIfNeeded()
    {
        if (_mode != CaptureMode.Undecided)
        {
            return;
        }

        if (!HttpContentTypes.IsInspectableResponse(response.ContentType))
        {
            SetUnsupported("unsupported_content_type");
            return;
        }

        var contentLength = response.ContentLength;
        if (contentLength > maxBodyBytes)
        {
            ObservedBytes = contentLength.Value;
            SetUnsupported("response_too_large");
            return;
        }

        _mode = CaptureMode.Buffer;
    }

    private void SetUnsupported(string reason)
    {
        UnsupportedReason = reason;
        _mode = blockUnsupported ? CaptureMode.Discard : CaptureMode.PassThrough;
    }

    private enum CaptureMode
    {
        Undecided,
        Buffer,
        PassThrough,
        Discard
    }
}
