using System;
using System.Globalization;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Limits the whole errors written to the log, so that a failing remote cannot fill the disk.
/// </summary>
internal sealed class ErrorLogLimit
{
    /// <summary>
    /// The most whole errors written in a minute.
    /// </summary>
    public const int MaxPerMinute = 20;

    /// <summary>
    /// The most characters of whole errors written in a minute.
    /// </summary>
    public const int MaxCharactersPerMinute = 512 * 1024;

    private readonly Lock _gate = new();
    private long _since;
    private int _written;
    private int _characters;

    /// <summary>
    /// Gets a whole error as it goes after a log line.
    /// </summary>
    /// <param name="detail">The whole error, empty when there is none.</param>
    /// <returns>The error, or a line that says it is not written.</returns>
    public string Of(string detail)
    {
        return detail.Length == 0 || MayWrite(detail.Length)
            ? detail
            : ErrorText.Block(string.Create(
                CultureInfo.InvariantCulture,
                $"More than {MaxPerMinute} whole errors or {MaxCharactersPerMinute} characters of them in a minute: the rest are not written"));
    }

    private bool MayWrite(int length)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (now - _since >= 60_000 || _written == 0)
            {
                _since = now;
                _written = 0;
                _characters = 0;
            }

            if (_written >= MaxPerMinute || _characters > MaxCharactersPerMinute - length)
            {
                return false;
            }

            _written++;
            _characters += length;
            return true;
        }
    }
}
