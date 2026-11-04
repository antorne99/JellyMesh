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
    /// The text shown to the user when no server has the title within reach, local or remote (decision 17).
    /// </summary>
    public const string Text = "Server non raggiungibile";

    /// <summary>
    /// The text shown to the user when every source refused for its stream limit: the server is fine, so <see cref="Text"/>
    /// would be wrong, and trying later can work.
    /// </summary>
    public const string LimitText = "Limite di stream raggiunto sul server di origine. Riprova più tardi.";

    /// <summary>
    /// The text shown to the user when a server that has the title answered but nothing could serve it, so that
    /// <see cref="Text"/> would be false.
    /// </summary>
    public const string FailedText = "Riproduzione non disponibile al momento. Riprova più tardi.";

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
    /// Chooses the text for a playback nothing can serve.
    /// </summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The text.</returns>
    public static string TextOf(PlaybackOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.AtStreamLimit)
        {
            return LimitText;
        }

        return outcome.AnyReachable ? FailedText : Text;
    }

    /// <summary>
    /// Builds the refusal, and schedules the message for after the response.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="deviceId">The device of the caller.</param>
    /// <param name="text">The text to show, one of the fixed ones of this class.</param>
    /// <returns>The result.</returns>
    public IActionResult Refuse(HttpContext context, string deviceId, string text = Text)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnCompleted(() => NotifyAsync(deviceId, text));
        return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
    }

    private async Task NotifyAsync(string deviceId, string text)
    {
        // With a timeout the web client shows a toast, above its own dialog, instead of a second dialog.
        var arguments = new Dictionary<string, string> { ["Header"] = Header, ["Text"] = text, ["TimeoutMs"] = ToastMilliseconds };
        var command = new GeneralCommand(arguments) { Name = GeneralCommandType.DisplayMessage };
        try
        {
            await _sessionManager.SendMessageToUserDeviceSessions(deviceId, SessionMessageType.GeneralCommand, command, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The refusal has already gone out, and a client without a websocket is normal.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Could not send the refusal message");
        }
    }
}
