using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace _1Rad.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceExtraCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("b2c3d4e5-f6a7-4b6c-9d0e-1f2a3b4c5d6e"));

            migrationBuilder.AddColumn<string>(
                name: "BillingMode",
                schema: "dbo",
                table: "SubscriptionPlans",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Subscription");

            migrationBuilder.AddColumn<string>(
                name: "Edition",
                schema: "dbo",
                table: "SubscriptionPlans",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "RIS+PACS");

            migrationBuilder.AddColumn<int>(
                name: "IncludedStorageGb",
                schema: "dbo",
                table: "SubscriptionPlans",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCustom",
                schema: "dbo",
                table: "SubscriptionPlans",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxSites",
                schema: "dbo",
                table: "SubscriptionPlans",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxUsers",
                schema: "dbo",
                table: "SubscriptionPlans",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Modules",
                schema: "dbo",
                table: "SubscriptionPlans",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "RIS,PACS");

            migrationBuilder.AddColumn<decimal>(
                name: "PerGbOveragePrice",
                schema: "dbo",
                table: "SubscriptionPlans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PerStudyPrice",
                schema: "dbo",
                table: "SubscriptionPlans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Tier",
                schema: "dbo",
                table: "SubscriptionPlans",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Starter");

            migrationBuilder.AddColumn<string>(
                name: "Modules",
                schema: "dbo",
                table: "SubscriptionPaymentRequests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlanId",
                schema: "dbo",
                table: "SubscriptionPaymentRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StorageOverageAmount",
                schema: "dbo",
                table: "SubscriptionPaymentRequests",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "StorageOverageGb",
                schema: "dbo",
                table: "SubscriptionPaymentRequests",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<Guid>(
                name: "AppointmentId",
                schema: "dbo",
                table: "StudyAssets",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "AppointmentServiceId",
                schema: "dbo",
                table: "StudyAssets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExtractionAttempts",
                schema: "dbo",
                table: "StudyAssets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExtractionCompletedAt",
                schema: "dbo",
                table: "StudyAssets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractionError",
                schema: "dbo",
                table: "StudyAssets",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractionLeaseOwner",
                schema: "dbo",
                table: "StudyAssets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExtractionLeaseUntil",
                schema: "dbo",
                table: "StudyAssets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExtractionNextAttemptAt",
                schema: "dbo",
                table: "StudyAssets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractionPhase",
                schema: "dbo",
                table: "StudyAssets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExtractionProcessedSlices",
                schema: "dbo",
                table: "StudyAssets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ExtractionSliceCount",
                schema: "dbo",
                table: "StudyAssets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExtractionStartedAt",
                schema: "dbo",
                table: "StudyAssets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractionStatus",
                schema: "dbo",
                table: "StudyAssets",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExtractionTotalSlices",
                schema: "dbo",
                table: "StudyAssets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ImagingStudyId",
                schema: "dbo",
                table: "StudyAssets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StorageBytes",
                schema: "dbo",
                table: "StudyAssets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceDisbursementId",
                schema: "dbo",
                table: "StaffLeaveRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EncashmentBonus",
                schema: "dbo",
                table: "SalaryDisbursements",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "EncashmentDays",
                schema: "dbo",
                table: "SalaryDisbursements",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ExtraPay",
                schema: "dbo",
                table: "SalaryDisbursements",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ExtraPayReason",
                schema: "dbo",
                table: "SalaryDisbursements",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceCategory",
                schema: "dbo",
                table: "RefreshTokens",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceName",
                schema: "dbo",
                table: "RefreshTokens",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IpAddress",
                schema: "dbo",
                table: "RefreshTokens",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                schema: "dbo",
                table: "RefreshTokens",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoggedOutReason",
                schema: "dbo",
                table: "RefreshTokens",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                schema: "dbo",
                table: "RefreshTokens",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                schema: "dbo",
                table: "RefreshTokens",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Degree",
                schema: "dbo",
                table: "Referrers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "dbo",
                table: "Referrers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                schema: "dbo",
                table: "Referrers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDoctor",
                schema: "dbo",
                table: "Referrers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "MergedIntoId",
                schema: "dbo",
                table: "Referrers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Specialty",
                schema: "dbo",
                table: "Referrers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupportedByDoctor",
                schema: "dbo",
                table: "Referrers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "dbo",
                table: "Referrers",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "AppointmentServiceId",
                schema: "dbo",
                table: "ReferralCommissions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                schema: "dbo",
                table: "ReferralCommissions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "dbo",
                table: "ReferralCommissions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaidBy",
                schema: "dbo",
                table: "ReferralCommissions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayeeAddress",
                schema: "dbo",
                table: "ReferralCommissions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayeeContact",
                schema: "dbo",
                table: "ReferralCommissions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayeeEmail",
                schema: "dbo",
                table: "ReferralCommissions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayeeName",
                schema: "dbo",
                table: "ReferralCommissions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "dbo",
                table: "ReferralCommissions",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                schema: "dbo",
                table: "ReferralCommissions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Block",
                schema: "dbo",
                table: "Patients",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "dbo",
                table: "Patients",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameNormalized",
                schema: "dbo",
                table: "Patients",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "dbo",
                table: "Patients",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<decimal>(
                name: "AdditionalCharges",
                schema: "dbo",
                table: "Invoices",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "AdditionalChargesReason",
                schema: "dbo",
                table: "Invoices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "dbo",
                table: "Invoices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFree",
                schema: "dbo",
                table: "Invoices",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "dbo",
                table: "Invoices",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "AppointmentServiceId",
                schema: "dbo",
                table: "InvoiceItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFree",
                schema: "dbo",
                table: "InvoiceItems",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BillingMode",
                schema: "dbo",
                table: "HospitalSubscriptions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Subscription");

            migrationBuilder.AddColumn<int>(
                name: "IncludedStorageGb",
                schema: "dbo",
                table: "HospitalSubscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxSites",
                schema: "dbo",
                table: "HospitalSubscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxUsers",
                schema: "dbo",
                table: "HospitalSubscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Modules",
                schema: "dbo",
                table: "HospitalSubscriptions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "RIS,PACS");

            migrationBuilder.AddColumn<DateTime>(
                name: "PacsRemovedAt",
                schema: "dbo",
                table: "HospitalSubscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PerStudyPrice",
                schema: "dbo",
                table: "HospitalSubscriptions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "dbo",
                table: "Expenses",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "dbo",
                table: "Expenses",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AlterColumn<Guid>(
                name: "AppointmentId",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "AppointmentServiceId",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImagingStudyId",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedAt",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SignedByUserId",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedContentHash",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignerCredentials",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignerName",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArrivedAt",
                schema: "dbo",
                table: "Appointments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "dbo",
                table: "Appointments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredAt",
                schema: "dbo",
                table: "Appointments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LatestCommentAt",
                schema: "dbo",
                table: "Appointments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestCommentAuthorName",
                schema: "dbo",
                table: "Appointments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OverdueAcknowledgedAt",
                schema: "dbo",
                table: "Appointments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OverdueAcknowledgedBy",
                schema: "dbo",
                table: "Appointments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                schema: "dbo",
                table: "Appointments",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "dbo",
                table: "Appointments",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScanStartedAt",
                schema: "dbo",
                table: "Appointments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupportedByDoctor",
                schema: "dbo",
                table: "Appointments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "dbo",
                table: "Appointments",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "AppointmentComments",
                schema: "dbo",
                columns: table => new
                {
                    AppointmentCommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentComments", x => x.AppointmentCommentId);
                    table.ForeignKey(
                        name: "FK_AppointmentComments_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalSchema: "dbo",
                        principalTable: "Appointments",
                        principalColumn: "AppointmentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentServices",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceChargeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ServiceName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Modality = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReferralCutValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsFree = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ScanStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ScanCompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReportedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TechnicianId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TechnicianComments = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppointmentServices_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalSchema: "dbo",
                        principalTable: "Appointments",
                        principalColumn: "AppointmentId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppointmentServices_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppointmentServices_ServiceCharges_ServiceChargeId",
                        column: x => x.ServiceChargeId,
                        principalSchema: "dbo",
                        principalTable: "ServiceCharges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CreditTransactions",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvoiceDisplayId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PaymentMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditTransactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyKeys",
                schema: "dbo",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ResponseStatus = table.Column<int>(type: "int", nullable: false),
                    ResponseBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseContentType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyKeys", x => new { x.Key, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "ImagingStudies",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StudyInstanceUID = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PatientName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    DicomPatientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Modality = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StudyDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StudyDescription = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    AccessionNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Received"),
                    MatchStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Unmatched"),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AppointmentServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReadyAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImagingStudies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImagingStudies_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalSchema: "dbo",
                        principalTable: "Appointments",
                        principalColumn: "AppointmentId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ImagingStudies_Patients_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "dbo",
                        principalTable: "Patients",
                        principalColumn: "PatientId");
                });

            migrationBuilder.CreateTable(
                name: "InvoiceExtraCharges",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceExtraCharges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceExtraCharges_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "dbo",
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RadAiQuestionLogs",
                schema: "dbo",
                columns: table => new
                {
                    RadAiQuestionLogId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AskedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Question = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    WasVoice = table.Column<bool>(type: "bit", nullable: false),
                    Page = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReplyLanguage = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    Covered = table.Column<bool>(type: "bit", nullable: false),
                    AnswerSnippet = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    CacheReadInputTokens = table.Column<int>(type: "int", nullable: false),
                    CacheHit = table.Column<bool>(type: "bit", nullable: false),
                    SavedInputTokens = table.Column<int>(type: "int", nullable: false),
                    SavedOutputTokens = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RadAiQuestionLogs", x => x.RadAiQuestionLogId);
                });

            migrationBuilder.CreateTable(
                name: "ReportAddenda",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AuthorCredentials = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportAddenda", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportAddenda_DiagnosticReports_ReportId",
                        column: x => x.ReportId,
                        principalSchema: "dbo",
                        principalTable: "DiagnosticReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportAuditEvents",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PreviousHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StudySliceIndexes",
                schema: "dbo",
                columns: table => new
                {
                    SliceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeriesUID = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SopInstanceUID = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    InstanceNumber = table.Column<int>(type: "int", nullable: true),
                    SeriesDescription = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Modality = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    BlobUrl = table.Column<string>(type: "nvarchar(700)", maxLength: 700, nullable: false),
                    BlobPath = table.Column<string>(type: "nvarchar(700)", maxLength: 700, nullable: false),
                    ThumbnailUrl = table.Column<string>(type: "nvarchar(700)", maxLength: 700, nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExtractedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudySliceIndexes", x => x.SliceId);
                    table.ForeignKey(
                        name: "FK_StudySliceIndexes_StudyAssets_AssetId",
                        column: x => x.AssetId,
                        principalSchema: "dbo",
                        principalTable: "StudyAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "dbo",
                table: "SubscriptionPlans",
                columns: new[] { "PlanId", "BillingMode", "CreatedAt", "DiscountPercentage", "DurationInDays", "Edition", "IncludedStorageGb", "IsActive", "IsCustom", "MaxSites", "MaxUsers", "Modules", "Name", "PerAdditionalDoctorPrice", "PerGbOveragePrice", "PerStudyPrice", "Price", "Tier" },
                values: new object[,]
                {
                    { new Guid("00a7c73c-ff9e-76f5-13a2-2c4cb8e9281f"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3260), 10m, 365, "PACS", 500, true, false, 1, 10, "PACS", "Yearly", 10800m, 50m, 0m, 75589m, "Growth" },
                    { new Guid("0de9d0ed-993d-225e-6702-398851d3adc0"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3213), 0m, 30, "PACS", 500, true, false, 1, 10, "PACS", "Monthly", 1000m, 50m, 0m, 6999m, "Growth" },
                    { new Guid("11e0d4b3-e807-d12d-2d38-50aaa1b8f95a"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(2752), 10m, 365, "RIS", null, true, false, 3, 10, "RIS", "Yearly", 10800m, 0m, 0m, 107989m, "Clinic" },
                    { new Guid("165646a6-ac81-0630-13fe-f6a61baa85f7"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(1435), 0m, 30, "RIS", null, true, false, 1, 2, "RIS", "Monthly", 1000m, 0m, 0m, 1999m, "Starter" },
                    { new Guid("1cfc5d0b-fe3c-fe85-3026-e2fc926baee1"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(2593), 0m, 30, "RIS", null, true, false, 1, 5, "RIS", "Monthly", 1000m, 0m, 0m, 4999m, "Growth" },
                    { new Guid("246b0044-ab5e-d6e3-df26-7f97af7a0858"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3309), 0m, 30, "PACS", 1024, true, false, 3, 20, "PACS", "Monthly", 1000m, 50m, 0m, 14999m, "Clinic" },
                    { new Guid("2b489007-a478-376a-41d9-245dbe5cea3b"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3131), 10m, 365, "PACS", 100, true, false, 1, 5, "PACS", "Yearly", 10800m, 50m, 0m, 32389m, "Starter" },
                    { new Guid("41a22183-ed11-3a9b-459f-46d1b7897033"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(2964), 0m, 30, "PACS", 100, true, false, 1, 5, "PACS", "Monthly", 1000m, 50m, 0m, 2999m, "Starter" },
                    { new Guid("5657c6b5-1f7e-1ebe-ab47-e7dbe6bdfee7"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3981), 10m, 365, "RIS+PACS", 1024, true, false, 3, 10, "RIS,PACS", "Yearly", 10800m, 50m, 0m, 215989m, "Clinic" },
                    { new Guid("5ad9fd92-f4f8-d078-0401-27f180e32907"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3747), 0m, 30, "RIS+PACS", 100, true, false, 1, 2, "RIS,PACS", "Monthly", 1000m, 50m, 0m, 3999m, "Starter" },
                    { new Guid("5c28b2f6-ba45-3b76-d708-1f566c3e3961"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3356), 10m, 365, "PACS", 1024, true, false, 3, 20, "PACS", "Yearly", 10800m, 50m, 0m, 161989m, "Clinic" },
                    { new Guid("6c31c887-aa1f-d9db-0f38-c90dcf2e1756"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3889), 10m, 365, "RIS+PACS", 500, true, false, 1, 5, "RIS,PACS", "Yearly", 10800m, 50m, 0m, 107989m, "Growth" },
                    { new Guid("703e9bdd-37a9-1fdd-fde5-d23d079f7c00"), "PerStudy", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(2827), 0m, 30, "RIS", null, true, false, null, null, "RIS", "PAYG", 0m, 0m, 8m, 0m, "PAYG" },
                    { new Guid("84374fbd-de78-3b73-1c49-0991509b7739"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(2472), 10m, 365, "RIS", null, true, false, 1, 2, "RIS", "Yearly", 10800m, 0m, 0m, 21589m, "Starter" },
                    { new Guid("98bc1afe-411f-c444-5b2f-584bd74fbfc5"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(4193), 0m, 30, "RIS+PACS", null, true, true, null, null, "RIS,PACS", "Custom", 0m, 50m, 0m, 0m, "Chain" },
                    { new Guid("9abe2292-71f9-8be7-9d9e-fc2fbbf2ba2e"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(2646), 10m, 365, "RIS", null, true, false, 1, 5, "RIS", "Yearly", 10800m, 0m, 0m, 53989m, "Growth" },
                    { new Guid("b0025879-623d-c9f3-baef-5a95d0b8e950"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3692), 0m, 30, "PACS", null, true, true, null, null, "PACS", "Custom", 0m, 50m, 0m, 0m, "Chain" },
                    { new Guid("c09bf2f1-32c4-a24d-a81f-58038b03924c"), "PerStudy", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(4135), 0m, 30, "RIS+PACS", null, true, false, null, null, "RIS,PACS", "PAYG", 0m, 0m, 25m, 0m, "PAYG" },
                    { new Guid("cbff0dac-2c40-5f58-94a4-c6a7c88677f6"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(2908), 0m, 30, "RIS", null, true, true, null, null, "RIS", "Custom", 0m, 0m, 0m, 0m, "Chain" },
                    { new Guid("d2a840a5-c3cc-dc91-5a49-0cac53d18cf1"), "PerStudy", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3404), 0m, 30, "PACS", null, true, false, null, null, "PACS", "PAYG", 0m, 0m, 15m, 0m, "PAYG" },
                    { new Guid("d5216c67-6cc5-b299-797b-396e27094266"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3938), 0m, 30, "RIS+PACS", 1024, true, false, 3, 10, "RIS,PACS", "Monthly", 1000m, 50m, 0m, 19999m, "Clinic" },
                    { new Guid("d913a413-9406-8c89-6483-d93c3c3648dd"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3797), 10m, 365, "RIS+PACS", 100, true, false, 1, 2, "RIS,PACS", "Yearly", 10800m, 50m, 0m, 43189m, "Starter" },
                    { new Guid("e147948a-044e-5abc-ea1e-f4677bcb2eef"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(2697), 0m, 30, "RIS", null, true, false, 3, 10, "RIS", "Monthly", 1000m, 0m, 0m, 9999m, "Clinic" },
                    { new Guid("e1d9b43d-813f-546e-feda-4fc27887b74b"), "Subscription", new DateTime(2026, 7, 18, 11, 27, 5, 297, DateTimeKind.Utc).AddTicks(3843), 0m, 30, "RIS+PACS", 500, true, false, 1, 5, "RIS,PACS", "Monthly", 1000m, 50m, 0m, 9999m, "Growth" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_StudyAssets_AppointmentServiceId",
                schema: "dbo",
                table: "StudyAssets",
                column: "AppointmentServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_StudyAssets_ExtractionStatus",
                schema: "dbo",
                table: "StudyAssets",
                column: "ExtractionStatus",
                filter: "[ExtractionStatus] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StudyAssets_ImagingStudyId",
                schema: "dbo",
                table: "StudyAssets",
                column: "ImagingStudyId",
                filter: "[ImagingStudyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StaffLeaveRequests_SourceDisbursementId",
                schema: "dbo",
                table: "StaffLeaveRequests",
                column: "SourceDisbursementId");

            migrationBuilder.CreateIndex(
                name: "IX_Referrers_MergedIntoId",
                schema: "dbo",
                table: "Referrers",
                column: "MergedIntoId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCommissions_AppointmentServiceId",
                schema: "dbo",
                table: "ReferralCommissions",
                column: "AppointmentServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItems_AppointmentServiceId",
                schema: "dbo",
                table: "InvoiceItems",
                column: "AppointmentServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticReports_AppointmentServiceId",
                schema: "dbo",
                table: "DiagnosticReports",
                column: "AppointmentServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticReports_ImagingStudyId",
                schema: "dbo",
                table: "DiagnosticReports",
                column: "ImagingStudyId",
                unique: true,
                filter: "[ImagingStudyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentComments_AppointmentId_CreatedAt",
                schema: "dbo",
                table: "AppointmentComments",
                columns: new[] { "AppointmentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentServices_AppointmentId",
                schema: "dbo",
                table: "AppointmentServices",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentServices_Hospital_UpdatedAt",
                schema: "dbo",
                table: "AppointmentServices",
                columns: new[] { "HospitalId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentServices_ServiceChargeId",
                schema: "dbo",
                table: "AppointmentServices",
                column: "ServiceChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditTransactions_HospitalId_PatientId",
                schema: "dbo",
                table: "CreditTransactions",
                columns: new[] { "HospitalId", "PatientId" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditTransactions_HospitalId_UpdatedAt",
                schema: "dbo",
                table: "CreditTransactions",
                columns: new[] { "HospitalId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyKeys_ExpiresAt",
                schema: "dbo",
                table: "IdempotencyKeys",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ImagingStudies_AppointmentId",
                schema: "dbo",
                table: "ImagingStudies",
                column: "AppointmentId",
                filter: "[AppointmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ImagingStudies_HospitalId_AccessionNumber",
                schema: "dbo",
                table: "ImagingStudies",
                columns: new[] { "HospitalId", "AccessionNumber" },
                filter: "[AccessionNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ImagingStudies_HospitalId_CreatedAt",
                schema: "dbo",
                table: "ImagingStudies",
                columns: new[] { "HospitalId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImagingStudies_HospitalId_MatchStatus",
                schema: "dbo",
                table: "ImagingStudies",
                columns: new[] { "HospitalId", "MatchStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_ImagingStudies_HospitalId_StudyInstanceUID",
                schema: "dbo",
                table: "ImagingStudies",
                columns: new[] { "HospitalId", "StudyInstanceUID" },
                unique: true,
                filter: "[StudyInstanceUID] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ImagingStudies_PatientId",
                schema: "dbo",
                table: "ImagingStudies",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceExtraCharges_InvoiceId",
                schema: "dbo",
                table: "InvoiceExtraCharges",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_RadAiQuestionLogs_HospitalId_Covered",
                schema: "dbo",
                table: "RadAiQuestionLogs",
                columns: new[] { "HospitalId", "Covered" });

            migrationBuilder.CreateIndex(
                name: "IX_RadAiQuestionLogs_HospitalId_CreatedAt",
                schema: "dbo",
                table: "RadAiQuestionLogs",
                columns: new[] { "HospitalId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportAddenda_ReportId_SortOrder",
                schema: "dbo",
                table: "ReportAddenda",
                columns: new[] { "ReportId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportAuditEvents_ReportId_Timestamp",
                schema: "dbo",
                table: "ReportAuditEvents",
                columns: new[] { "ReportId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_StudySliceIndexes_AppointmentId",
                schema: "dbo",
                table: "StudySliceIndexes",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_StudySliceIndexes_AssetId_SeriesUID_InstanceNumber",
                schema: "dbo",
                table: "StudySliceIndexes",
                columns: new[] { "AssetId", "SeriesUID", "InstanceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_StudySliceIndexes_HospitalId",
                schema: "dbo",
                table: "StudySliceIndexes",
                column: "HospitalId");

            migrationBuilder.AddForeignKey(
                name: "FK_DiagnosticReports_AppointmentServices_AppointmentServiceId",
                schema: "dbo",
                table: "DiagnosticReports",
                column: "AppointmentServiceId",
                principalSchema: "dbo",
                principalTable: "AppointmentServices",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_DiagnosticReports_ImagingStudies_ImagingStudyId",
                schema: "dbo",
                table: "DiagnosticReports",
                column: "ImagingStudyId",
                principalSchema: "dbo",
                principalTable: "ImagingStudies",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceItems_AppointmentServices_AppointmentServiceId",
                schema: "dbo",
                table: "InvoiceItems",
                column: "AppointmentServiceId",
                principalSchema: "dbo",
                principalTable: "AppointmentServices",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ReferralCommissions_AppointmentServices_AppointmentServiceId",
                schema: "dbo",
                table: "ReferralCommissions",
                column: "AppointmentServiceId",
                principalSchema: "dbo",
                principalTable: "AppointmentServices",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Referrers_Referrers_MergedIntoId",
                schema: "dbo",
                table: "Referrers",
                column: "MergedIntoId",
                principalSchema: "dbo",
                principalTable: "Referrers",
                principalColumn: "ReferrerId");

            migrationBuilder.AddForeignKey(
                name: "FK_StudyAssets_AppointmentServices_AppointmentServiceId",
                schema: "dbo",
                table: "StudyAssets",
                column: "AppointmentServiceId",
                principalSchema: "dbo",
                principalTable: "AppointmentServices",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_StudyAssets_ImagingStudies_ImagingStudyId",
                schema: "dbo",
                table: "StudyAssets",
                column: "ImagingStudyId",
                principalSchema: "dbo",
                principalTable: "ImagingStudies",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DiagnosticReports_AppointmentServices_AppointmentServiceId",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropForeignKey(
                name: "FK_DiagnosticReports_ImagingStudies_ImagingStudyId",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceItems_AppointmentServices_AppointmentServiceId",
                schema: "dbo",
                table: "InvoiceItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ReferralCommissions_AppointmentServices_AppointmentServiceId",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropForeignKey(
                name: "FK_Referrers_Referrers_MergedIntoId",
                schema: "dbo",
                table: "Referrers");

            migrationBuilder.DropForeignKey(
                name: "FK_StudyAssets_AppointmentServices_AppointmentServiceId",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropForeignKey(
                name: "FK_StudyAssets_ImagingStudies_ImagingStudyId",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropTable(
                name: "AppointmentComments",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "AppointmentServices",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ApprovalRequests");

            migrationBuilder.DropTable(
                name: "CreditTransactions",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "IdempotencyKeys",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ImagingStudies",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "InvoiceExtraCharges",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "RadAiQuestionLogs",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ReportAddenda",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ReportAuditEvents",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "StudySliceIndexes",
                schema: "dbo");

            migrationBuilder.DropIndex(
                name: "IX_StudyAssets_AppointmentServiceId",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropIndex(
                name: "IX_StudyAssets_ExtractionStatus",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropIndex(
                name: "IX_StudyAssets_ImagingStudyId",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropIndex(
                name: "IX_StaffLeaveRequests_SourceDisbursementId",
                schema: "dbo",
                table: "StaffLeaveRequests");

            migrationBuilder.DropIndex(
                name: "IX_Referrers_MergedIntoId",
                schema: "dbo",
                table: "Referrers");

            migrationBuilder.DropIndex(
                name: "IX_ReferralCommissions_AppointmentServiceId",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceItems_AppointmentServiceId",
                schema: "dbo",
                table: "InvoiceItems");

            migrationBuilder.DropIndex(
                name: "IX_DiagnosticReports_AppointmentServiceId",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropIndex(
                name: "IX_DiagnosticReports_ImagingStudyId",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("00a7c73c-ff9e-76f5-13a2-2c4cb8e9281f"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("0de9d0ed-993d-225e-6702-398851d3adc0"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("11e0d4b3-e807-d12d-2d38-50aaa1b8f95a"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("165646a6-ac81-0630-13fe-f6a61baa85f7"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("1cfc5d0b-fe3c-fe85-3026-e2fc926baee1"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("246b0044-ab5e-d6e3-df26-7f97af7a0858"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("2b489007-a478-376a-41d9-245dbe5cea3b"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("41a22183-ed11-3a9b-459f-46d1b7897033"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("5657c6b5-1f7e-1ebe-ab47-e7dbe6bdfee7"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("5ad9fd92-f4f8-d078-0401-27f180e32907"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("5c28b2f6-ba45-3b76-d708-1f566c3e3961"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("6c31c887-aa1f-d9db-0f38-c90dcf2e1756"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("703e9bdd-37a9-1fdd-fde5-d23d079f7c00"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("84374fbd-de78-3b73-1c49-0991509b7739"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("98bc1afe-411f-c444-5b2f-584bd74fbfc5"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("9abe2292-71f9-8be7-9d9e-fc2fbbf2ba2e"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("b0025879-623d-c9f3-baef-5a95d0b8e950"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("c09bf2f1-32c4-a24d-a81f-58038b03924c"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("cbff0dac-2c40-5f58-94a4-c6a7c88677f6"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("d2a840a5-c3cc-dc91-5a49-0cac53d18cf1"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("d5216c67-6cc5-b299-797b-396e27094266"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("d913a413-9406-8c89-6483-d93c3c3648dd"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("e147948a-044e-5abc-ea1e-f4677bcb2eef"));

            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "SubscriptionPlans",
                keyColumn: "PlanId",
                keyValue: new Guid("e1d9b43d-813f-546e-feda-4fc27887b74b"));

            migrationBuilder.DropColumn(
                name: "BillingMode",
                schema: "dbo",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "Edition",
                schema: "dbo",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "IncludedStorageGb",
                schema: "dbo",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "IsCustom",
                schema: "dbo",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "MaxSites",
                schema: "dbo",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "MaxUsers",
                schema: "dbo",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "Modules",
                schema: "dbo",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "PerGbOveragePrice",
                schema: "dbo",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "PerStudyPrice",
                schema: "dbo",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "Tier",
                schema: "dbo",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "Modules",
                schema: "dbo",
                table: "SubscriptionPaymentRequests");

            migrationBuilder.DropColumn(
                name: "PlanId",
                schema: "dbo",
                table: "SubscriptionPaymentRequests");

            migrationBuilder.DropColumn(
                name: "StorageOverageAmount",
                schema: "dbo",
                table: "SubscriptionPaymentRequests");

            migrationBuilder.DropColumn(
                name: "StorageOverageGb",
                schema: "dbo",
                table: "SubscriptionPaymentRequests");

            migrationBuilder.DropColumn(
                name: "AppointmentServiceId",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionAttempts",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionCompletedAt",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionError",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionLeaseOwner",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionLeaseUntil",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionNextAttemptAt",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionPhase",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionProcessedSlices",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionSliceCount",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionStartedAt",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionStatus",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ExtractionTotalSlices",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "ImagingStudyId",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "StorageBytes",
                schema: "dbo",
                table: "StudyAssets");

            migrationBuilder.DropColumn(
                name: "SourceDisbursementId",
                schema: "dbo",
                table: "StaffLeaveRequests");

            migrationBuilder.DropColumn(
                name: "EncashmentBonus",
                schema: "dbo",
                table: "SalaryDisbursements");

            migrationBuilder.DropColumn(
                name: "EncashmentDays",
                schema: "dbo",
                table: "SalaryDisbursements");

            migrationBuilder.DropColumn(
                name: "ExtraPay",
                schema: "dbo",
                table: "SalaryDisbursements");

            migrationBuilder.DropColumn(
                name: "ExtraPayReason",
                schema: "dbo",
                table: "SalaryDisbursements");

            migrationBuilder.DropColumn(
                name: "DeviceCategory",
                schema: "dbo",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "DeviceName",
                schema: "dbo",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                schema: "dbo",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                schema: "dbo",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "LoggedOutReason",
                schema: "dbo",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "SessionId",
                schema: "dbo",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                schema: "dbo",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "Degree",
                schema: "dbo",
                table: "Referrers");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "dbo",
                table: "Referrers");

            migrationBuilder.DropColumn(
                name: "Email",
                schema: "dbo",
                table: "Referrers");

            migrationBuilder.DropColumn(
                name: "IsDoctor",
                schema: "dbo",
                table: "Referrers");

            migrationBuilder.DropColumn(
                name: "MergedIntoId",
                schema: "dbo",
                table: "Referrers");

            migrationBuilder.DropColumn(
                name: "Specialty",
                schema: "dbo",
                table: "Referrers");

            migrationBuilder.DropColumn(
                name: "SupportedByDoctor",
                schema: "dbo",
                table: "Referrers");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "dbo",
                table: "Referrers");

            migrationBuilder.DropColumn(
                name: "AppointmentServiceId",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropColumn(
                name: "PaidBy",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropColumn(
                name: "PayeeAddress",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropColumn(
                name: "PayeeContact",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropColumn(
                name: "PayeeEmail",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropColumn(
                name: "PayeeName",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                schema: "dbo",
                table: "ReferralCommissions");

            migrationBuilder.DropColumn(
                name: "Block",
                schema: "dbo",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "dbo",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "NameNormalized",
                schema: "dbo",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "dbo",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "AdditionalCharges",
                schema: "dbo",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "AdditionalChargesReason",
                schema: "dbo",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "dbo",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "IsFree",
                schema: "dbo",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "dbo",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "AppointmentServiceId",
                schema: "dbo",
                table: "InvoiceItems");

            migrationBuilder.DropColumn(
                name: "IsFree",
                schema: "dbo",
                table: "InvoiceItems");

            migrationBuilder.DropColumn(
                name: "BillingMode",
                schema: "dbo",
                table: "HospitalSubscriptions");

            migrationBuilder.DropColumn(
                name: "IncludedStorageGb",
                schema: "dbo",
                table: "HospitalSubscriptions");

            migrationBuilder.DropColumn(
                name: "MaxSites",
                schema: "dbo",
                table: "HospitalSubscriptions");

            migrationBuilder.DropColumn(
                name: "MaxUsers",
                schema: "dbo",
                table: "HospitalSubscriptions");

            migrationBuilder.DropColumn(
                name: "Modules",
                schema: "dbo",
                table: "HospitalSubscriptions");

            migrationBuilder.DropColumn(
                name: "PacsRemovedAt",
                schema: "dbo",
                table: "HospitalSubscriptions");

            migrationBuilder.DropColumn(
                name: "PerStudyPrice",
                schema: "dbo",
                table: "HospitalSubscriptions");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "dbo",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "dbo",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "AppointmentServiceId",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropColumn(
                name: "ImagingStudyId",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropColumn(
                name: "SignedAt",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropColumn(
                name: "SignedByUserId",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropColumn(
                name: "SignedContentHash",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropColumn(
                name: "SignerCredentials",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropColumn(
                name: "SignerName",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "dbo",
                table: "DiagnosticReports");

            migrationBuilder.DropColumn(
                name: "ArrivedAt",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "DeliveredAt",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "LatestCommentAt",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "LatestCommentAuthorName",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "OverdueAcknowledgedAt",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "OverdueAcknowledgedBy",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "ScanStartedAt",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "SupportedByDoctor",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "dbo",
                table: "Appointments");

            migrationBuilder.AlterColumn<Guid>(
                name: "AppointmentId",
                schema: "dbo",
                table: "StudyAssets",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "AppointmentId",
                schema: "dbo",
                table: "DiagnosticReports",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.InsertData(
                schema: "dbo",
                table: "SubscriptionPlans",
                columns: new[] { "PlanId", "CreatedAt", "DiscountPercentage", "DurationInDays", "IsActive", "Name", "PerAdditionalDoctorPrice", "Price" },
                values: new object[,]
                {
                    { new Guid("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d"), new DateTime(2026, 5, 22, 12, 52, 36, 771, DateTimeKind.Utc).AddTicks(4763), 0m, 30, true, "Monthly", 1000m, 4999m },
                    { new Guid("b2c3d4e5-f6a7-4b6c-9d0e-1f2a3b4c5d6e"), new DateTime(2026, 5, 22, 12, 52, 36, 771, DateTimeKind.Utc).AddTicks(4782), 10m, 365, true, "Yearly", 10800m, 53988m }
                });
        }
    }
}
