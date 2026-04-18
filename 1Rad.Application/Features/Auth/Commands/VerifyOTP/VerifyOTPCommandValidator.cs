using FluentValidation;

namespace _1Rad.Application.Features.Auth.Commands.VerifyOTP;

public class VerifyOTPCommandValidator : AbstractValidator<VerifyOTPCommand>
{
    public VerifyOTPCommandValidator()
    {
        RuleFor(x => x.Mobile)
            .NotEmpty().WithMessage("Mobile number is required.")
            .Matches(@"^\d{10}$").WithMessage("Mobile number must be exactly 10 digits.");

        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("OTP code is required.")
            .Length(6).WithMessage("OTP code must be exactly 6 characters.");
    }
}
