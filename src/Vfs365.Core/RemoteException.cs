namespace Vfs365.Core;

/// <summary>Gone: a delta link that expired (410); the caller starts over.</summary>
public enum RemoteError { NotFound, AccessDenied, Throttled, Locked, Conflict, Unavailable, Gone, Other }

/// <summary>A failed call to Microsoft 365, classified so front ends can map it without knowing HTTP.</summary>
public class RemoteException(RemoteError error, string message) : Exception(message)
{
    public RemoteError Error { get; } = error;
}
