namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Outcome of a remote operation. Failures carry only a <see cref="RemoteFailure"/>, so that no
/// exception text, which can hold a remote address, travels further.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class RemoteResult<T>
{
    internal RemoteResult(T? value, RemoteFailure failure, int? status = null)
    {
        Value = value;
        Failure = failure;
        Status = status;
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
    /// Carries this failure and its status over to a result of another type.
    /// </summary>
    /// <typeparam name="TOther">The other value type.</typeparam>
    /// <returns>The failed result.</returns>
    public RemoteResult<TOther> FailAs<TOther>() => new(default, Failure, Status);

    /// <summary>
    /// Describes the failure for a log line: its kind and, when there is one, the HTTP status. Nothing the remote wrote.
    /// </summary>
    /// <returns>The description.</returns>
    public string Describe() => Status is { } status ? Failure + " (HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : Failure.ToString();
}
