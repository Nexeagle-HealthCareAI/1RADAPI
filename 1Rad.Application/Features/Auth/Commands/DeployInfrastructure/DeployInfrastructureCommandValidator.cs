using FluentValidation;
using _1Rad.Domain.Constants;

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

        RuleFor(x => x.Degree)
            .NotEmpty()
            .When(x => x.RoleId == 1 || x.RoleId == 3)
            .WithMessage("Medical Degree is required for doctor registration.");

        RuleFor(x => x.LicenseNo)
            .NotEmpty()
            .When(x => x.RoleId == 1 || x.RoleId == 3)
            .WithMessage("Medical Registration License No is required for doctor registration.");

        // Tactical Conditional Validation: CMO/Doctor Requirement
        RuleFor(x => x.Degree)
            .NotEmpty()
            .When(x => x.RoleId == RoleConstants.AdminDoctor)
            .WithMessage("Medical Degree is mandatory for Chief Medical Officers.");

        RuleFor(x => x.LicenseNo)
            .NotEmpty()
            .When(x => x.RoleId == RoleConstants.AdminDoctor)
            .WithMessage("Medical License Number is mandatory for clinical accountability.");
    }
}
