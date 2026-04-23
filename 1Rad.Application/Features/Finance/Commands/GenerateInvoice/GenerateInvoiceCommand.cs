using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.GenerateInvoice;

public record GenerateInvoiceCommand : IRequest<Guid>
{
    public Guid? AppointmentId { get; init; }
    public Guid PatientId { get; init; }
    public List<InvoiceItemDto> Items { get; init; } = new();
}

public record InvoiceItemDto(string Description, decimal Amount, int Quantity);

public class GenerateInvoiceCommandHandler : IRequestHandler<GenerateInvoiceCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public GenerateInvoiceCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> Handle(GenerateInvoiceCommand request, CancellationToken cancellationToken)
    {
        try
        {
            // Validate items
            if (request.Items == null || !request.Items.Any())
            {
                throw new ArgumentException("Invoice must contain at least one item.", nameof(request.Items));
            }

            // Validate item amounts
            foreach (var item in request.Items)
            {
                if (item.Amount <= 0)
                {
                    throw new ArgumentException($"Item '{item.Description}' has invalid amount. Amount must be greater than zero.");
                }
                if (item.Quantity <= 0)
                {
                    throw new ArgumentException($"Item '{item.Description}' has invalid quantity. Quantity must be greater than zero.");
                }
            }

            var patient = await _context.Patients
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.PatientId == request.PatientId, cancellationToken);
            
            if (patient == null)
            {
                throw new KeyNotFoundException($"Patient with ID '{request.PatientId}' not found in the system (Checked global registry).");
            }

            var hospitalId = _context.UserContext.HospitalId;
            
            // Fallback to patient's hospital ID if context is missing (common in some automated background tasks)
            if (hospitalId == Guid.Empty)
            {
                hospitalId = patient.HospitalId;
            }

            // Verify patient belongs to the hospital
            if (patient.HospitalId != hospitalId)
            {
                throw new UnauthorizedAccessException($"Patient does not belong to your hospital.");
            }

            // Verify appointment if provided
            if (request.AppointmentId.HasValue)
            {
                var appointment = await _context.Appointments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId.Value, cancellationToken);
                
                if (appointment == null)
                {
                    throw new KeyNotFoundException($"Appointment with ID '{request.AppointmentId}' not found in global registry.");
                }

                if (appointment.PatientId != request.PatientId)
                {
                    throw new InvalidOperationException("Appointment does not belong to the specified patient.");
                }
            }

            var invoice = new Invoice
            {
                AppointmentId = request.AppointmentId,
                PatientId = request.PatientId,
                HospitalId = hospitalId,
                InvoiceId = $"INV-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 4).ToUpper()}",
                TotalAmount = request.Items.Sum(x => x.Amount * x.Quantity),
                PaidAmount = 0,
                Status = "PENDING",
                CreatedAt = DateTime.UtcNow
            };

            foreach (var item in request.Items)
            {
                invoice.Items.Add(new InvoiceItem
                {
                    Description = item.Description,
                    Amount = item.Amount,
                    Quantity = item.Quantity
                });
            }

            _context.Invoices.Add(invoice);
            await _context.SaveChangesAsync(cancellationToken);

            return invoice.Id;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var messages = new List<string>();
            var current = ex;
            while (current != null)
            {
                messages.Add($"{current.GetType().Name}: {current.Message}");
                current = current.InnerException;
            }
            throw new Exception($"[FINANCE_ERR] {string.Join(" | ", messages)}", ex);
        }
    }
}
