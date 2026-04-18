using _1Rad.Domain.Common;
using _1Rad.Domain.Entities;

namespace _1Rad.Domain.Events;

public class UserPasswordChangedEvent : BaseEvent
{
    public User User { get; }

    public UserPasswordChangedEvent(User user)
    {
        User = user;
    }
}
