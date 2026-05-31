namespace _1Rad.Application.Common.Exceptions;

// Thrown by command handlers when SaveChangesAsync detects a stale
// concurrency token (the row has changed since the client last read it).
// The Server property carries the canonical current state so the API
// layer can return it inside a 409 Conflict response — the frontend
// uses it to overwrite the user's local view AND offer an "Undo" action
// for the next 30 seconds.
public class OccConflictException : Exception
{
    public object Server { get; }

    public OccConflictException(object serverState, string? message = null)
        : base(message ?? "The record was modified by another user.")
    {
        Server = serverState;
    }
}
