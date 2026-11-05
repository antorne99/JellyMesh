using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public class StreamRelayTests
{
    private static RemoteContent Content(Stream body, string? type = "video/mp4", long? length = null)
    {
        return new RemoteContent(HttpStatusCode.OK, new RemoteContentHeaders(type, length, null, null), body);
    }

    private static DefaultHttpContext Context(Stream target)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = target;
        return context;
    }

    [Fact]
    public async Task A_remote_that_goes_silent_breaks_the_connection_at_the_stall_limit()
    {
        using var content = Content(new SilentStream());
        var aborted = new AbortRecorder();
        var context = Context(new MemoryStream());
        context.Features.Set<IHttpRequestLifetimeFeature>(aborted);

        await StreamRelay.CopyAsync(content, context, TimeSpan.FromMilliseconds(100), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(aborted.Aborted);
    }

    [Fact]
    public async Task A_client_that_reads_slowly_is_not_a_stall()
    {
        using var content = Content(new MemoryStream(new byte[300 * 1024]));
        using var slow = new SlowStream(TimeSpan.FromMilliseconds(150));
        var aborted = new AbortRecorder();
        var context = Context(slow);
        context.Features.Set<IHttpRequestLifetimeFeature>(aborted);

        await StreamRelay.CopyAsync(content, context, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(aborted.Aborted);
        Assert.Equal(300 * 1024, slow.Written);
    }

    [Fact]
    public async Task Nothing_is_held_back_while_the_remote_sends()
    {
        using var body = new OneReadThenSilence(new byte[] { 1, 2, 3 });
        using var content = Content(body);
        using var target = new MemoryStream();
        var context = Context(target);
        context.Features.Set<IHttpRequestLifetimeFeature>(new AbortRecorder());

        var relay = StreamRelay.CopyAsync(content, context, TimeSpan.FromSeconds(30), CancellationToken.None);
        await body.FirstReadDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var waited = 0; waited < 200 && target.Length == 0; waited++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(3, target.Length);
        body.Finish();
        await relay;
    }

    [Fact]
    public async Task A_client_that_leaves_ends_the_copy_quietly()
    {
        using var content = Content(new SilentStream());
        var aborted = new AbortRecorder();
        var context = Context(new MemoryStream());
        context.Features.Set<IHttpRequestLifetimeFeature>(aborted);
        using var left = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await StreamRelay.CopyAsync(content, context, TimeSpan.FromSeconds(30), left.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(aborted.Aborted);
    }

    [Theory]
    [InlineData("text/html", "application/octet-stream")]
    [InlineData("text/html; charset=utf-8", "application/octet-stream")]
    [InlineData("application/xhtml+xml", "application/octet-stream")]
    [InlineData("image/svg+xml", "application/octet-stream")]
    [InlineData("application/javascript", "application/octet-stream")]
    [InlineData("video/mp4", "video/mp4")]
    [InlineData("video/MP2T", "video/MP2T")]
    [InlineData("audio/aac", "audio/aac")]
    [InlineData("text/vtt; charset=utf-8", "text/vtt; charset=utf-8")]
    [InlineData("application/vnd.apple.mpegurl", "application/vnd.apple.mpegurl")]
    public async Task A_remote_cannot_turn_a_media_route_into_a_page_of_this_server(string declared, string expected)
    {
        using var content = Content(new MemoryStream(), declared);
        var context = Context(new MemoryStream());

        await StreamRelay.CopyAsync(content, context, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(expected, context.Response.ContentType);
    }

    private sealed class AbortRecorder : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }

    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OneReadThenSilence : Stream
    {
        private readonly TaskCompletionSource _finished = new();
        private readonly byte[] _bytes;
        private bool _read;

        public OneReadThenSilence(byte[] bytes) => _bytes = bytes;

        public TaskCompletionSource FirstReadDone { get; } = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Finish() => _finished.SetResult();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_read)
            {
                _read = true;
                _bytes.CopyTo(buffer);
                FirstReadDone.SetResult();
                return _bytes.Length;
            }

            await _finished.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SlowStream : MemoryStream
    {
        private readonly TimeSpan _delay;

        public SlowStream(TimeSpan delay) => _delay = delay;

        public long Written { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            Written += buffer.Length;
        }
    }
}
