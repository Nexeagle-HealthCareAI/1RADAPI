using _1Rad.Application.Interfaces;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;

namespace _1Rad.UnitTests.Schema;

/// <summary>
/// Gap analysis P3-3: the EF model must match a database built from 1RADDatabase's
/// scripts. Two columns sets (StaffLeaveRequests.SourceDisbursementId and the RadAI
/// token columns) existed only in code and broke the Leave module and RadAI logging.
///
/// CI builds the database from scripts, exports INFORMATION_SCHEMA.COLUMNS as CSV
/// ("Table,Column" per line) and points SCHEMA_CHECK_CSV at it. Without that variable
/// (normal local test runs) this test does nothing.
/// </summary>
public class SchemaDriftTests
{
    [Fact]
    public void Every_mapped_column_exists_in_the_script_built_database()
    {
        var csv = Environment.GetEnvironmentVariable("SCHEMA_CHECK_CSV");
        if (string.IsNullOrWhiteSpace(csv)) return; // only runs in CI

        var dbColumns = File.ReadAllLines(csv)
            .Select(l => l.Split(','))
            .Where(p => p.Length >= 2 && !string.IsNullOrWhiteSpace(p[0]))
            .Select(p => (Table: p[0].Trim(), Column: p[1].Trim()))
            .ToHashSet();
        dbColumns.Should().NotBeEmpty("the schema snapshot should list the database's columns");
        var dbTables = dbColumns.Select(c => c.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dbSet = dbColumns.Select(c => $"{c.Table}.{c.Column}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=schema-check;Database=none;Trusted_Connection=False")
            .Options;
        using var ctx = new ApplicationDbContext(options, Mock.Of<IPublisher>(), Mock.Of<IUserContext>());

        var missing = new List<string>();
        foreach (var entity in ctx.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table == null || entity.GetViewName() != null) continue;
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());

            if (!dbTables.Contains(table))
            {
                missing.Add($"table {table} (entity {entity.ClrType.Name})");
                continue;
            }
            foreach (var prop in entity.GetProperties())
            {
                var column = prop.GetColumnName(store);
                if (column == null) continue;
                if (!dbSet.Contains($"{table}.{column}"))
                    missing.Add($"{table}.{column} (entity {entity.ClrType.Name}.{prop.Name})");
            }
        }

        missing.Should().BeEmpty(
            "every entity property needs a column created by a numbered script in 1RADDatabase/schema — add a guarded NN_*.sql");
    }
}
