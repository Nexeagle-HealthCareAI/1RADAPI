using _1Rad.Application.Interfaces;

namespace _1Rad.Infrastructure.Authentication;

/// <summary>Scoped (one per request). Scopes nest: closing one puts back the centre before it.</summary>
public class ActingCentre : IActingCentre
{
    private readonly Stack<Guid> _stack = new();
    public Guid? HospitalId => _stack.Count > 0 ? _stack.Peek() : null;

    public IDisposable Act(Guid hospitalId)
    {
        if (hospitalId == Guid.Empty) throw new ArgumentException("A centre is needed.", nameof(hospitalId));
        _stack.Push(hospitalId);
        return new Scope(this);
    }

    private sealed class Scope : IDisposable
    {
        private ActingCentre? _owner;
        public Scope(ActingCentre owner) => _owner = owner;
        public void Dispose()
        {
            if (_owner == null) return;
            if (_owner._stack.Count > 0) _owner._stack.Pop();
            _owner = null;
        }
    }
}
