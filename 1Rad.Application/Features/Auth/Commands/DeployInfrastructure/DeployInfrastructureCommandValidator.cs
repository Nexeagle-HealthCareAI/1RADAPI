using FluentValidation;

namespace _1Rad.Application.Features.Auth.Commands.DeployInfrastructure;

public class DeployInfrastructureCommandValidator : AbstractValidator<DeployInfrastructureCommand>
{
    public DeployInfrastructureCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required.");

        RuleFor(x => x.ChainName)
            .NotEmpty().WithMessage("Chain Name is required.")
            .MaximumLength(255).WithMessage("Chain Name cannot exceed 255 characters.");

        RuleFor(x => x.HospitalName)
            .NotEmpty().WithMessage("Hospital Name is required.")
            .MaximumLength(255).WithMessage("Hospital Name cannot exceed 255 characters.");

        RuleFor(x => x.HospitalAddress)
            .NotEmpty().WithMessage("Hospital Address is required.");

        RuleFor(x => x.RoleId)
            .GreaterThan(0).WithMessage("Please select a valid Role.");
    }
}
