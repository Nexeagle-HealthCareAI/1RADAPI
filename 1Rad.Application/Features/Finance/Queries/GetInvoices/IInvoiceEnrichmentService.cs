using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace _1Rad.Application.Features.Finance.Queries.GetInvoices;

public interface IInvoiceEnrichmentService
{
    Task EnrichInvoicesAsync(List<InvoiceDto> invoices, CancellationToken cancellationToken);
}
