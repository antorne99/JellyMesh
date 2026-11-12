namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Creates <see cref="RemoteResult{T}"/> values.
/// </summary>
public static class RemoteResult
{
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The result.</returns>
    public static RemoteResult<T> Success<T>(T value) => new(value, RemoteFailure.None);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="failure">The failure.</param>
    /// <returns>The result.</returns>
    public static RemoteResult<T> Fail<T>(RemoteFailure failure) => new(default, failure);

    /// <summary>
    /// Creates a failed result that still names what it is about, such as the server that already stands for a remote.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="failure">The failure.</param>
    /// <param name="value">What the failure is about.</param>
    /// <returns>The result.</returns>
    public static RemoteResult<T> Fail<T>(RemoteFailure failure, T value) => new(value, failure);
}
