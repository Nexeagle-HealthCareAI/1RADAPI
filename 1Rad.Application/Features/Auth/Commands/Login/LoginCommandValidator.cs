using FluentValidation;

namespace _1Rad.Application.Features.Auth.Commands.Login;

/// <summary>
/// Catches null/empty inputs at the framework boundary so the handler can
/// assume non-empty values. Without this, BCrypt.Verify throws an
/// ArgumentException when the password or PasswordHash is null, which
/// surfaces as a confusing generic 400.
/// </summary>
public class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Identifier)
            .NotEmpty().WithMessage("Email or mobile number is required.")
            .MaximumLength(256).WithMessage("Identifier is too long.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(1).WithMessage("Password cannot be empty.")
            .MaximumLength(512).WithMessage("Password is too long.");
    }
}
