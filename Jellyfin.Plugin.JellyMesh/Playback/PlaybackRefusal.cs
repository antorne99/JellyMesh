using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Refuses a playback that nothing can serve: HTTP 503 with a fixed body, then a message to the device. The message only
/// shows on clients that display it, and it never names a server.
/// </summary>
public sealed class PlaybackRefusal
{
    /// <summary>
    /// The text shown to the user.
    /// </summary>
    public const string Text = "Server non raggiungibile";

    private const string Header = "JellyMesh";
    private const string ToastMilliseconds = "8000";

    private readonly ISessionManager _sessionManager;
    private readonly ILogger<PlaybackRefusal> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackRefusal"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackRefusal(ISessionManager sessionManager, ILogger<PlaybackRefusal> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Builds the refusal, and schedules the message for after the response.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="deviceId">The device of the caller.</param>
    /// <returns>The result.</returns>
    public IActionResult Refuse(HttpContext context, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnCompleted(() => NotifyAsync(deviceId));
        return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
    }

    private async Task NotifyAsync(string deviceId)
    {
        // With a timeout the web client shows a toast, above its own dialog, instead of a second dialog.
        var arguments = new Dictionary<string, string> { ["Header"] = Header, ["Text"] = Text, ["TimeoutMs"] = ToastMilliseconds };
        var command = new GeneralCommand(arguments) { Name = GeneralCommandType.DisplayMessage };
        try
        {
            await _sessionManager.SendMessageToUserDeviceSessions(deviceId, SessionMessageType.GeneralCommand, command, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The refusal has already gone out, and a client without a websocket is normal.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogDebug(exception, "Could not send the refusal message");
        }
    }
}
