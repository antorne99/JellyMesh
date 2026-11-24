namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Outcome of a remote operation. <see cref="Error"/> tells the whole failure to the log and to administrators; clients get
/// nothing but what follows from the <see cref="RemoteFailure"/>.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class RemoteResult<T>
{
    internal RemoteResult(T? value, RemoteFailure failure, int? status = null, RemoteError? error = null)
    {
        Value = value;
        Failure = failure;
        Status = status;
        Error = error;
    }

    /// <summary>
    /// Gets the value of a successful operation.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// Gets the failure, <see cref="RemoteFailure.None"/> on success.
    /// </summary>
    public RemoteFailure Failure { get; }

    /// <summary>
    /// Gets a value indicating whether the operation succeeded.
    /// </summary>
    public bool IsSuccess => Failure == RemoteFailure.None;

    /// <summary>
    /// Gets the HTTP status the remote answered a failure with, null when no answer came or it did not decide the failure.
    /// </summary>
    public int? Status { get; }

    /// <summary>
    /// Gets the whole story of a failure, null when no call was made. It holds the address of the remote.
    /// </summary>
    public RemoteError? Error { get; }

    /// <summary>
    /// Gets the whole story of a failure as it goes after a log line, empty when there is none.
    /// </summary>
    internal string Detail => Error is null ? string.Empty : ErrorText.Block(Error.ToString());

    /// <summary>
    /// Carries this failure, with its status and story, over to a result of another type.
    /// </summary>
    /// <typeparam name="TOther">The other value type.</typeparam>
    /// <returns>The failed result.</returns>
    public RemoteResult<TOther> FailAs<TOther>() => new(default, Failure, Status, Error);

    /// <summary>
    /// Describes the failure for a log line: its kind and, when there is one, the HTTP status. Nothing the remote wrote.
    /// </summary>
    /// <returns>The description.</returns>
    public string Describe() => Status is { } status ? Failure + " (HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : Failure.ToString();
}
