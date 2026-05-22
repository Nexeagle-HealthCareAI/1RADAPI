using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace _1Rad.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionPaymentRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d"),
                column: "CreatedAt",
                value: new DateTime(2026, 5, 22, 12, 52, 36, 771, DateTimeKind.Utc).AddTicks(4763));

            migrationBuilder.UpdateData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("b2c3d4e5-f6a7-4b6c-9d0e-1f2a3b4c5d6e"),
                column: "CreatedAt",
                value: new DateTime(2026, 5, 22, 12, 52, 36, 771, DateTimeKind.Utc).AddTicks(4782));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d"),
                column: "CreatedAt",
                value: new DateTime(2026, 5, 22, 12, 47, 20, 128, DateTimeKind.Utc).AddTicks(983));

            migrationBuilder.UpdateData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("b2c3d4e5-f6a7-4b6c-9d0e-1f2a3b4c5d6e"),
                column: "CreatedAt",
                value: new DateTime(2026, 5, 22, 12, 47, 20, 128, DateTimeKind.Utc).AddTicks(1030));
        }
    }
}
