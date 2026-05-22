using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace _1Rad.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dbo");

            migrationBuilder.CreateTable(
                name: "HospitalGroups",
                schema: "dbo",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HospitalGroups", x => x.GroupId);
                });

            migrationBuilder.CreateTable(
                name: "OTPVerifications",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Identifier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsUsed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OTPVerifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "dbo",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.RoleId);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionPlans",
                schema: "dbo",
                columns: table => new
                {
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DurationInDays = table.Column<int>(type: "int", nullable: false),
                    DiscountPercentage = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PerAdditionalDoctorPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPlans", x => x.PlanId);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "dbo",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Mobile = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Password = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsVerified = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Specialization = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Degree = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LicenseNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PreferredReportingMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Structured"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Hospitals",
                schema: "dbo",
                columns: table => new
                {
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HospitalName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    HospitalAddress = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GSTIN = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    RegistrationNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PAN = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    NABHNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsAutoBillingEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hospitals", x => x.HospitalId);
                    table.ForeignKey(
                        name: "FK_Hospitals_HospitalGroups_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "dbo",
                        principalTable: "HospitalGroups",
                        principalColumn: "GroupId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByIp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedByIp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReplacedByToken = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomRoles",
                schema: "dbo",
                columns: table => new
                {
                    CustomRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomRoles", x => x.CustomRoleId);
                    table.ForeignKey(
                        name: "FK_CustomRoles_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HospitalLeavePolicies",
                schema: "dbo",
                columns: table => new
                {
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaveTypesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false, defaultValue: "[]"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HospitalLeavePolicies", x => x.PolicyId);
                    table.ForeignKey(
                        name: "FK_HospitalLeavePolicies_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HospitalSubscriptions",
                schema: "dbo",
                columns: table => new
                {
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BillingCycle = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Trial"),
                    StartDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsTrial = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsLocked = table.Column<bool>(type: "bit", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NotificationSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActivatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActivatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HospitalSubscriptions", x => x.SubscriptionId);
                    table.ForeignKey(
                        name: "FK_HospitalSubscriptions_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HospitalSubscriptions_SubscriptionPlans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "dbo",
                        principalTable: "SubscriptionPlans",
                        principalColumn: "PlanId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PrescriptionProtocols",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HeaderMargin = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LeftMargin = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RightMargin = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BottomMargin = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FontSize = table.Column<int>(type: "int", nullable: false),
                    FontColor = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FontFamily = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LetterheadBlobUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OverflowBackgroundMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true, defaultValue: "REUSE"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrescriptionProtocols", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrescriptionProtocols_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrescriptionProtocols_Users_DoctorId",
                        column: x => x.DoctorId,
                        principalSchema: "dbo",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Referrers",
                schema: "dbo",
                columns: table => new
                {
                    ReferrerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Contact = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Referrers", x => x.ReferrerId);
                    table.ForeignKey(
                        name: "FK_Referrers_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportingKeywords",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ReplacementText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DoctorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportingKeywords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportingKeywords_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReportingKeywords_Users_DoctorId",
                        column: x => x.DoctorId,
                        principalSchema: "dbo",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportTemplates",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Modality = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportTemplates_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffMembers",
                schema: "dbo",
                columns: table => new
                {
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Mobile = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Designation = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Department = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EmploymentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Full-Time"),
                    Specialization = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Degree = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LicenseNo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    JoiningDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Active"),
                    PhotoUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PhotoPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffMembers", x => x.StaffId);
                    table.ForeignKey(
                        name: "FK_StaffMembers_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionPaymentRequests",
                schema: "dbo",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BillingCycle = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PayerName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    PayerContact = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TransactionReference = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    PaymentMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaymentGatewayOrderId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PaymentGatewayResponse = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPaymentRequests", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_SubscriptionPaymentRequests_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserHospitalMappings",
                schema: "dbo",
                columns: table => new
                {
                    MappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserHospitalMappings", x => x.MappingId);
                    table.ForeignKey(
                        name: "FK_UserHospitalMappings_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserHospitalMappings_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomRolePermissions",
                schema: "dbo",
                columns: table => new
                {
                    CustomRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoutePath = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomRolePermissions", x => new { x.CustomRoleId, x.RoutePath });
                    table.ForeignKey(
                        name: "FK_CustomRolePermissions_CustomRoles_CustomRoleId",
                        column: x => x.CustomRoleId,
                        principalSchema: "dbo",
                        principalTable: "CustomRoles",
                        principalColumn: "CustomRoleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Patients",
                schema: "dbo",
                columns: table => new
                {
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Mobile = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Age = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Village = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    District = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Address = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PatientIdentifier = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceOfInfo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReferrerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Patients", x => x.PatientId);
                    table.ForeignKey(
                        name: "FK_Patients_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Patients_Referrers_ReferrerId",
                        column: x => x.ReferrerId,
                        principalSchema: "dbo",
                        principalTable: "Referrers",
                        principalColumn: "ReferrerId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ReferralCommissions",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferrerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferrerName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Modality = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CommissionAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    AccumulatedTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TransactionDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ServiceDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Pending"),
                    PaymentDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReferenceNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferralCommissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReferralCommissions_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReferralCommissions_Referrers_ReferrerId",
                        column: x => x.ReferrerId,
                        principalSchema: "dbo",
                        principalTable: "Referrers",
                        principalColumn: "ReferrerId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ServiceCharges",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modality = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ServiceName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReferralCutValue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceCharges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceCharges_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServiceCharges_ReportTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalSchema: "dbo",
                        principalTable: "ReportTemplates",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SalaryRevisions",
                schema: "dbo",
                columns: table => new
                {
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    BasicPay = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    Hra = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    Travel = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    OtherAllowances = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    PfDeduction = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    Tds = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    OtherDeductions = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryRevisions", x => x.RevisionId);
                    table.ForeignKey(
                        name: "FK_SalaryRevisions_StaffMembers_StaffId",
                        column: x => x.StaffId,
                        principalSchema: "dbo",
                        principalTable: "StaffMembers",
                        principalColumn: "StaffId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffAttendance",
                schema: "dbo",
                columns: table => new
                {
                    AttendanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttendanceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "present"),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MarkedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MarkedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffAttendance", x => x.AttendanceId);
                    table.ForeignKey(
                        name: "FK_StaffAttendance_StaffMembers_StaffId",
                        column: x => x.StaffId,
                        principalSchema: "dbo",
                        principalTable: "StaffMembers",
                        principalColumn: "StaffId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffDocuments",
                schema: "dbo",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "Other"),
                    FileName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FileSizeBytes = table.Column<int>(type: "int", nullable: true),
                    BlobUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    BlobPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BlobContainer = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    VerificationStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Pending"),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffDocuments", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_StaffDocuments_StaffMembers_StaffId",
                        column: x => x.StaffId,
                        principalSchema: "dbo",
                        principalTable: "StaffMembers",
                        principalColumn: "StaffId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffLeaveRequests",
                schema: "dbo",
                columns: table => new
                {
                    LeaveRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaveType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Days = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    AppliedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffLeaveRequests", x => x.LeaveRequestId);
                    table.ForeignKey(
                        name: "FK_StaffLeaveRequests_StaffMembers_StaffId",
                        column: x => x.StaffId,
                        principalSchema: "dbo",
                        principalTable: "StaffMembers",
                        principalColumn: "StaffId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffMemberRoles",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffMemberRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffMemberRoles_StaffMembers_StaffId",
                        column: x => x.StaffId,
                        principalSchema: "dbo",
                        principalTable: "StaffMembers",
                        principalColumn: "StaffId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserHospitalCustomRoles",
                schema: "dbo",
                columns: table => new
                {
                    MappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserHospitalCustomRoles", x => new { x.MappingId, x.CustomRoleId });
                    table.ForeignKey(
                        name: "FK_UserHospitalCustomRoles_CustomRoles_CustomRoleId",
                        column: x => x.CustomRoleId,
                        principalSchema: "dbo",
                        principalTable: "CustomRoles",
                        principalColumn: "CustomRoleId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserHospitalCustomRoles_UserHospitalMappings_MappingId",
                        column: x => x.MappingId,
                        principalSchema: "dbo",
                        principalTable: "UserHospitalMappings",
                        principalColumn: "MappingId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserHospitalRoles",
                schema: "dbo",
                columns: table => new
                {
                    MappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserHospitalRoles", x => new { x.MappingId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserHospitalRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "dbo",
                        principalTable: "Roles",
                        principalColumn: "RoleId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserHospitalRoles_UserHospitalMappings_MappingId",
                        column: x => x.MappingId,
                        principalSchema: "dbo",
                        principalTable: "UserHospitalMappings",
                        principalColumn: "MappingId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Appointments",
                schema: "dbo",
                columns: table => new
                {
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Mobile = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Service = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Modality = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DateTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Doctor = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ReferredBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReferredContact = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TechnicianComments = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TechnicianId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ScannedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DailyTokenNumber = table.Column<int>(type: "int", nullable: true),
                    DelayReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReportProgressStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appointments", x => x.AppointmentId);
                    table.ForeignKey(
                        name: "FK_Appointments_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Appointments_Patients_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "dbo",
                        principalTable: "Patients",
                        principalColumn: "PatientId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalaryDisbursements",
                schema: "dbo",
                columns: table => new
                {
                    DisbursementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Draft"),
                    GrossPay = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    NetPay = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    StructureGross = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    StructureNet = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    LwpDays = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    LwpDeduction = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    PerDayRate = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    PaidLeaveInMonth = table.Column<int>(type: "int", nullable: false),
                    LwpLeaveInMonth = table.Column<int>(type: "int", nullable: false),
                    AttendanceJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PaymentMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "bank"),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PaidOnDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryDisbursements", x => x.DisbursementId);
                    table.ForeignKey(
                        name: "FK_SalaryDisbursements_SalaryRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalSchema: "dbo",
                        principalTable: "SalaryRevisions",
                        principalColumn: "RevisionId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SalaryDisbursements_StaffMembers_StaffId",
                        column: x => x.StaffId,
                        principalSchema: "dbo",
                        principalTable: "StaffMembers",
                        principalColumn: "StaffId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticReports",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Findings = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Impression = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Advice = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsFinalized = table.Column<bool>(type: "bit", nullable: false),
                    FinalizedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReportPdfUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ReportingMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Structured"),
                    FieldCount = table.Column<int>(type: "int", nullable: true, defaultValue: 0),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticReports_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalSchema: "dbo",
                        principalTable: "Appointments",
                        principalColumn: "AppointmentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiagnosticReports_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiagnosticReports_ReportTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalSchema: "dbo",
                        principalTable: "ReportTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DiagnosticReports_Users_DoctorId",
                        column: x => x.DoctorId,
                        principalSchema: "dbo",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    GrossAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PaidAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ServiceDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReferralCutValue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CentreDiscount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ReferrerDiscount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    InstitutionalDeduction = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invoices_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalSchema: "dbo",
                        principalTable: "Appointments",
                        principalColumn: "AppointmentId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Invoices_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Invoices_Patients_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "dbo",
                        principalTable: "Patients",
                        principalColumn: "PatientId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StudyAssets",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BlobUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FileType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TechnicianComments = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudyAssets_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalSchema: "dbo",
                        principalTable: "Appointments",
                        principalColumn: "AppointmentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Expenses",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ReferenceNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    VendorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CostCenter = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Paid"),
                    TransactionDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkedDisbursementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Expenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Expenses_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Expenses_SalaryDisbursements_LinkedDisbursementId",
                        column: x => x.LinkedDisbursementId,
                        principalSchema: "dbo",
                        principalTable: "SalaryDisbursements",
                        principalColumn: "DisbursementId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticReportFields",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SectionName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    FieldName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    FieldValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticReportFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticReportFields_DiagnosticReports_ReportId",
                        column: x => x.ReportId,
                        principalSchema: "dbo",
                        principalTable: "DiagnosticReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceItems",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceItems_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "dbo",
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HospitalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Hospitals_HospitalId",
                        column: x => x.HospitalId,
                        principalSchema: "dbo",
                        principalTable: "Hospitals",
                        principalColumn: "HospitalId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Payments_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "dbo",
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "dbo",
                table: "Roles",
                columns: new[] { "RoleId", "RoleName" },
                values: new object[,]
                {
                    { 1, "AdminDoctor" },
                    { 2, "Admin" },
                    { 3, "Doctor" },
                    { 4, "Technician" },
                    { 5, "Receptionist" },
                    { 6, "Accountant" }
                });

            migrationBuilder.InsertData(
                schema: "dbo",
                table: "SubscriptionPlans",
                columns: new[] { "PlanId", "CreatedAt", "DiscountPercentage", "DurationInDays", "IsActive", "Name", "PerAdditionalDoctorPrice", "Price" },
                values: new object[,]
                {
                    { new Guid("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d"), new DateTime(2026, 5, 22, 12, 47, 20, 128, DateTimeKind.Utc).AddTicks(983), 0m, 30, true, "Monthly", 1000m, 4999m },
                    { new Guid("b2c3d4e5-f6a7-4b6c-9d0e-1f2a3b4c5d6e"), new DateTime(2026, 5, 22, 12, 47, 20, 128, DateTimeKind.Utc).AddTicks(1030), 10m, 365, true, "Yearly", 10800m, 53988m }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_HospitalId",
                schema: "dbo",
                table: "Appointments",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_PatientId",
                schema: "dbo",
                table: "Appointments",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomRoles_HospitalId_RoleName",
                schema: "dbo",
                table: "CustomRoles",
                columns: new[] { "HospitalId", "RoleName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticReportFields_ReportId",
                schema: "dbo",
                table: "DiagnosticReportFields",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticReports_AppointmentId",
                schema: "dbo",
                table: "DiagnosticReports",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticReports_DoctorId",
                schema: "dbo",
                table: "DiagnosticReports",
                column: "DoctorId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticReports_HospitalId",
                schema: "dbo",
                table: "DiagnosticReports",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticReports_TemplateId",
                schema: "dbo",
                table: "DiagnosticReports",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_HospitalId",
                schema: "dbo",
                table: "Expenses",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_LinkedDisbursementId",
                schema: "dbo",
                table: "Expenses",
                column: "LinkedDisbursementId");

            migrationBuilder.CreateIndex(
                name: "IX_HospitalLeavePolicies_HospitalId",
                schema: "dbo",
                table: "HospitalLeavePolicies",
                column: "HospitalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Hospitals_GroupId",
                schema: "dbo",
                table: "Hospitals",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_HospitalSubscriptions_HospitalId_Status",
                schema: "dbo",
                table: "HospitalSubscriptions",
                columns: new[] { "HospitalId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_HospitalSubscriptions_PlanId",
                schema: "dbo",
                table: "HospitalSubscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItems_InvoiceId",
                schema: "dbo",
                table: "InvoiceItems",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_AppointmentId",
                schema: "dbo",
                table: "Invoices",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_HospitalId",
                schema: "dbo",
                table: "Invoices",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_PatientId",
                schema: "dbo",
                table: "Invoices",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_HospitalId",
                schema: "dbo",
                table: "Patients",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_Mobile",
                schema: "dbo",
                table: "Patients",
                column: "Mobile");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_ReferrerId",
                schema: "dbo",
                table: "Patients",
                column: "ReferrerId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_HospitalId",
                schema: "dbo",
                table: "Payments",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_InvoiceId",
                schema: "dbo",
                table: "Payments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PrescriptionProtocols_DoctorId",
                schema: "dbo",
                table: "PrescriptionProtocols",
                column: "DoctorId");

            migrationBuilder.CreateIndex(
                name: "IX_PrescriptionProtocols_HospitalId",
                schema: "dbo",
                table: "PrescriptionProtocols",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCommissions_HospitalId",
                schema: "dbo",
                table: "ReferralCommissions",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCommissions_ReferrerId",
                schema: "dbo",
                table: "ReferralCommissions",
                column: "ReferrerId");

            migrationBuilder.CreateIndex(
                name: "IX_Referrers_HospitalId",
                schema: "dbo",
                table: "Referrers",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                schema: "dbo",
                table: "RefreshTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportingKeywords_DoctorId",
                schema: "dbo",
                table: "ReportingKeywords",
                column: "DoctorId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportingKeywords_HospitalId",
                schema: "dbo",
                table: "ReportingKeywords",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportTemplates_HospitalId",
                schema: "dbo",
                table: "ReportTemplates",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryDisbursements_HospitalId",
                schema: "dbo",
                table: "SalaryDisbursements",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryDisbursements_RevisionId",
                schema: "dbo",
                table: "SalaryDisbursements",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryDisbursements_StaffId_Month",
                schema: "dbo",
                table: "SalaryDisbursements",
                columns: new[] { "StaffId", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalaryRevisions_HospitalId",
                schema: "dbo",
                table: "SalaryRevisions",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryRevisions_StaffId_EffectiveFrom",
                schema: "dbo",
                table: "SalaryRevisions",
                columns: new[] { "StaffId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceCharges_HospitalId",
                schema: "dbo",
                table: "ServiceCharges",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceCharges_TemplateId",
                schema: "dbo",
                table: "ServiceCharges",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffAttendance_HospitalId_AttendanceDate",
                schema: "dbo",
                table: "StaffAttendance",
                columns: new[] { "HospitalId", "AttendanceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffAttendance_StaffId_AttendanceDate",
                schema: "dbo",
                table: "StaffAttendance",
                columns: new[] { "StaffId", "AttendanceDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffDocuments_StaffId",
                schema: "dbo",
                table: "StaffDocuments",
                column: "StaffId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffLeaveRequests_HospitalId",
                schema: "dbo",
                table: "StaffLeaveRequests",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffLeaveRequests_StaffId",
                schema: "dbo",
                table: "StaffLeaveRequests",
                column: "StaffId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffMemberRoles_StaffId_RoleName",
                schema: "dbo",
                table: "StaffMemberRoles",
                columns: new[] { "StaffId", "RoleName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_HospitalId_EmployeeCode",
                schema: "dbo",
                table: "StaffMembers",
                columns: new[] { "HospitalId", "EmployeeCode" },
                unique: true,
                filter: "[EmployeeCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StudyAssets_AppointmentId",
                schema: "dbo",
                table: "StudyAssets",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPaymentRequests_HospitalId_Status",
                schema: "dbo",
                table: "SubscriptionPaymentRequests",
                columns: new[] { "HospitalId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_UserHospitalCustomRoles_CustomRoleId",
                schema: "dbo",
                table: "UserHospitalCustomRoles",
                column: "CustomRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserHospitalMappings_HospitalId",
                schema: "dbo",
                table: "UserHospitalMappings",
                column: "HospitalId");

            migrationBuilder.CreateIndex(
                name: "IX_UserHospitalMappings_UserId_HospitalId",
                schema: "dbo",
                table: "UserHospitalMappings",
                columns: new[] { "UserId", "HospitalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserHospitalRoles_RoleId",
                schema: "dbo",
                table: "UserHospitalRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                schema: "dbo",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Mobile",
                schema: "dbo",
                table: "Users",
                column: "Mobile",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomRolePermissions",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "DiagnosticReportFields",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Expenses",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "HospitalLeavePolicies",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "HospitalSubscriptions",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "InvoiceItems",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "OTPVerifications",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Payments",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "PrescriptionProtocols",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ReferralCommissions",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "RefreshTokens",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ReportingKeywords",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ServiceCharges",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "StaffAttendance",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "StaffDocuments",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "StaffLeaveRequests",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "StaffMemberRoles",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "StudyAssets",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SubscriptionPaymentRequests",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "UserHospitalCustomRoles",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "UserHospitalRoles",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "DiagnosticReports",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SalaryDisbursements",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SubscriptionPlans",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Invoices",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "CustomRoles",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "UserHospitalMappings",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ReportTemplates",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SalaryRevisions",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Appointments",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "StaffMembers",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Patients",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Referrers",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Hospitals",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "HospitalGroups",
                schema: "dbo");
        }
    }
}
