using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Reads an answer into memory, never more than a limit. The only way a body of a remote gets there: it is read as it
/// arrives, so a declared length is no promise, and the caller's token also bounds a body that stops arriving.
/// </summary>
internal static class RemoteBody
{
    private const int ChunkBytes = 64 * 1024;

    /// <summary>
    /// Reads a body.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <param name="maxBytes">The most bytes accepted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bytes, null if there are too many.</returns>
    public static async Task<byte[]?> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[ChunkBytes];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return buffer.ToArray();
        }
    }
}
