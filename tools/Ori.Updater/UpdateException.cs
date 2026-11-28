namespace Ori.Updater;

/// <summary>
/// A rejected update (bad URL, manifest or hash). Kept public so tests can
/// assert that unsafe input is refused instead of silently accepted.
/// </summary>
public sealed class UpdateException(string message) : Exception(message);
