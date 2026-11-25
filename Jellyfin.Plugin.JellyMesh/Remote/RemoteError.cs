using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The whole story of a failed call to a remote, for the log and for administrators, never for clients: it holds the
/// address of the remote. Its texts hold no token and no password.
/// </summary>
/// <param name="Step">The step that failed.</param>
/// <param name="Request">The method and the URL.</param>
/// <param name="ElapsedMilliseconds">How long the call took.</param>
public sealed record RemoteError(string Step, string Request, long ElapsedMilliseconds)
{
    /// <summary>
    /// Gets the status of the answer with its reason, null when none came.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Gets what else is known of the answer, such as its type or where it redirects.
    /// </summary>
    public string? Answer { get; init; }

    /// <summary>
    /// Gets the body of the answer, cut and cleaned.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>
    /// Gets the local exception with its inner ones and their stack traces.
    /// </summary>
    public string? Exception { get; init; }

    /// <summary>
    /// Gets why the answer is refused, when its status does not say it.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Gets how many times the call was tried.
    /// </summary>
    public int Tries { get; init; } = 1;

    /// <summary>
    /// Writes the error on many lines.
    /// </summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append("Step: ").Append(Step);
        text.Append("\nRequest: ").Append(Request);
        text.Append("\nStatus: ").Append(Status ?? "no answer");
        text.Append(CultureInfo.InvariantCulture, $"\nDuration: {ElapsedMilliseconds} ms");
        if (Tries > 1)
        {
            text.Append(CultureInfo.InvariantCulture, $" (try {Tries}, the last one)");
        }

        Append(text, "Reason", Reason);
        Append(text, "Answer", Answer);
        AppendBlock(text, "Body", Body);
        AppendBlock(text, "Exception", Exception);
        return text.ToString();
    }

    private static void Append(StringBuilder text, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            text.Append('\n').Append(name).Append(": ").Append(value);
        }
    }

    private static void AppendBlock(StringBuilder text, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            text.Append('\n').Append(name).Append(':').Append(ErrorText.Block(value));
        }
    }
}
