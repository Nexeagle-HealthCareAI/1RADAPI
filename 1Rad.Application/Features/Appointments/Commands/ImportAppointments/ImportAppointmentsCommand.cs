using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ExcelDataReader;
using System.Data;

namespace _1Rad.Application.Features.Appointments.Commands.ImportAppointments;

public record ImportAppointmentsCommand(Stream FileStream) : IRequest<ImportResult>;

public record ImportResult(int SuccessCount, int FailureCount, List<string> Errors);

public class ImportAppointmentsCommandHandler : IRequestHandler<ImportAppointmentsCommand, ImportResult>
{
    private readonly IApplicationDbContext _context;

    public ImportAppointmentsCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ImportResult> Handle(ImportAppointmentsCommand request, CancellationToken cancellationToken)
    {
        int success = 0;
        int failure = 0;
        var errors = new List<string>();

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        using (var reader = ExcelReaderFactory.CreateReader(request.FileStream))
        {
            var result = reader.AsDataSet(new ExcelDataSetConfiguration()
            {
                ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = true }
            });

            var table = result.Tables[0];
            foreach (DataRow row in table.Rows)
            {
                try
                {
                    // Column Mapping (based on user request)
                    var ptid = row["Patientid"]?.ToString();
                    var name = row["Patientname"]?.ToString();
                    var mobile = row["patient contact"]?.ToString();
                    var referrerName = row["reffered By name"]?.ToString();
                    var referrerContact = row["Reffered bycontact"]?.ToString();
                    var referrerAddress = row["Reffered byAddress"]?.ToString();
                    var modality = row["Modality"]?.ToString();
                    var modalityType = row["Modality Type"]?.ToString();
                    var dateStr = row["Date"]?.ToString();

                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(mobile))
                    {
                        failure++;
                        errors.Add($"Row {table.Rows.IndexOf(row) + 2}: Name and Mobile are mandatory.");
                        continue;
                    }

                    // 1. Process Patient (Deduplication Logic)
                    var patient = await _context.Patients
                        .FirstOrDefaultAsync(p => p.FullName.ToLower() == name.ToLower() && p.Mobile == mobile, cancellationToken);

                    if (patient == null)
                    {
                        patient = new Patient
                        {
                            FullName = name,
                            Mobile = mobile,
                            PatientIdentifier = ptid ?? string.Empty,
                            HospitalId = _context.UserContext.HospitalId
                        };
                        _context.Patients.Add(patient);
                    }
                    else
                    {
                        // Identity synchronization
                        if (!string.IsNullOrEmpty(ptid)) patient.PatientIdentifier = ptid;
                    }

                    // 2. Process Referrer
                    Guid? referrerId = null;
                    if (!string.IsNullOrEmpty(referrerName))
                    {
                        var referrer = await _context.Referrers
                            .FirstOrDefaultAsync(r => r.Name.ToLower() == referrerName.ToLower(), cancellationToken);

                        if (referrer == null)
                        {
                            referrer = new Referrer
                            {
                                Name = referrerName,
                                Contact = referrerContact ?? "N/A",
                                Address = referrerAddress ?? "N/A"
                            };
                            _context.Referrers.Add(referrer);
                        }
                        referrerId = referrer.ReferrerId;
                    }

                    // 3. Create Appointment
                    DateTime.TryParse(dateStr, out DateTime appDate);
                    if (appDate == default) appDate = DateTime.Today;

                    var appCount = await _context.Appointments.CountAsync(cancellationToken);
                    var appointment = new Appointment
                    {
                        DisplayId = $"APP-{1010 + appCount + success}",
                        PatientId = patient.PatientId,
                        PatientName = patient.FullName,
                        Mobile = patient.Mobile,
                        Service = modalityType ?? modality ?? "RECON",
                        Modality = modality ?? "X-RAY",
                        DateTime = appDate,
                        Status = "COMPLETED", // Assuming imported clinical logs are historical/completed
                        Type = "In-Patient",
                        Doctor = "Imported Source",
                        ReferredBy = referrerName ?? "Self",
                        ReferredContact = referrerContact ?? string.Empty,
                        HospitalId = _context.UserContext.HospitalId
                    };

                    _context.Appointments.Add(appointment);
                    success++;
                }
                catch (Exception ex)
                {
                    failure++;
                    errors.Add($"Row {table.Rows.IndexOf(row) + 2}: {ex.Message}");
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new ImportResult(success, failure, errors);
    }
}
