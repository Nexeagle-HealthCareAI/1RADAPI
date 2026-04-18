using FluentValidation;

namespace _1Rad.Application.Features.Auth.Commands.SendOTP;

public class SendOTPCommandValidator : AbstractValidator<SendOTPCommand>
{
    public SendOTPCommandValidator()
    {
        RuleFor(x => x.Mobile)
            .NotEmpty().WithMessage("Mobile number is required.")
            .Matches(@"^\d{10}$").WithMessage("Mobile number must be exactly 10 digits.");
    }
}
