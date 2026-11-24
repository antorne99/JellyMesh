using System;
using System.Globalization;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Limits the whole errors written to the log, so that a failing remote cannot fill the disk.
/// </summary>
/// <param name="clock">The clock.</param>
internal sealed class ErrorLogLimit(TimeProvider? clock = null)
{
    /// <summary>
    /// The most whole errors written in a minute.
    /// </summary>
    public const int MaxPerMinute = 20;

    /// <summary>
    /// The most characters of whole errors written in a minute.
    /// </summary>
    public const int MaxCharactersPerMinute = 512 * 1024;

    /// <summary>
    /// The most characters of whole errors written in a day.
    /// </summary>
    public const int MaxCharactersPerDay = 50 * 1024 * 1024;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private long _minute;
    private int _minuteErrors;
    private int _minuteCharacters;
    private long _day;
    private int _dayCharacters;

    /// <summary>
    /// Gets a whole error as it goes after a log line.
    /// </summary>
    /// <param name="detail">The whole error, empty when there is none.</param>
    /// <returns>The error, or a note that says it is not written.</returns>
    public string Of(string detail)
    {
        return detail.Length == 0 || MayWrite(detail.Length)
            ? detail
            : string.Create(
                CultureInfo.InvariantCulture,
                $" [not written whole: over {MaxPerMinute} errors or {MaxCharactersPerMinute} characters in a minute, or {MaxCharactersPerDay} characters in a day]");
    }

    /// <summary>
    /// Gets a whole exception as it goes after a log line.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The exception, or a note that says it is not written.</returns>
    public string Of(Exception? exception) => Of(ErrorText.Of(exception));

    private bool MayWrite(int length)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (_minuteErrors == 0 || _clock.GetElapsedTime(_minute, now) >= TimeSpan.FromMinutes(1))
            {
                _minute = now;
                _minuteErrors = 0;
                _minuteCharacters = 0;
            }

            if (_dayCharacters == 0 || _clock.GetElapsedTime(_day, now) >= TimeSpan.FromDays(1))
            {
                _day = now;
                _dayCharacters = 0;
            }

            if (_minuteErrors >= MaxPerMinute || _minuteCharacters > MaxCharactersPerMinute - length || _dayCharacters > MaxCharactersPerDay - length)
            {
                return false;
            }

            _minuteErrors++;
            _minuteCharacters += length;
            _dayCharacters += length;
            return true;
        }
    }
}
