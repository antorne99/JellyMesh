namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Outcome of a remote operation. Failures carry only a <see cref="RemoteFailure"/>, so that no
/// exception text, which can hold a remote address, travels further.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class RemoteResult<T>
{
    internal RemoteResult(T? value, RemoteFailure failure)
    {
        Value = value;
        Failure = failure;
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
}
