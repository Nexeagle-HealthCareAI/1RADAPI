using MediatR;
using _1Rad.Domain.Entities;

namespace _1Rad.Domain.Events;

public class HospitalRegisteredEvent : INotification
{
    public HospitalRegisteredEvent(User user, Hospital hospital)
    {
        User = user;
        Hospital = hospital;
    }

    public User User { get; }
    public Hospital Hospital { get; }
}
