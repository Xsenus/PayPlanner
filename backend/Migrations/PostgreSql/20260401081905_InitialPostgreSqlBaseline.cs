using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class InitialPostgreSqlBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BaseCurrencies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BaseCurrencies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Contracts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    ValidUntil = table.Column<DateTime>(type: "date", nullable: true),
                    TerminationDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Countries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Countries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DealTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DealTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FormFieldRequirements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Section = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FieldKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormFieldRequirements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IncomeTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PaymentType = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncomeTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceItemNames",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DefaultPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceItemNames", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Number = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceUnits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceUnits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MoneyOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Direction = table.Column<string>(type: "text", nullable: false),
                    AccountingType = table.Column<string>(type: "text", nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CounterpartyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CategoryName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    PaymentOrderNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    RecognitionDate = table.Column<DateTime>(type: "date", nullable: true),
                    IsAdvancePayment = table.Column<bool>(type: "boolean", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PaidInvoiceNumber = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    ProjectName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DirectionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SalesPointName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PaymentPriority = table.Column<int>(type: "integer", nullable: true),
                    PaidAccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoneyOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OkinDictionaryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OkinDictionaryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OkpdtrDictionaryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OkpdtrDictionaryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OksoDictionaryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OksoDictionaryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationForms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationForms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceLanguages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLanguages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Services",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Services", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxSystemModes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxSystemModes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxSystems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxSystems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Timezones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Timezones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    Section = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CanView = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CanCreate = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CanEdit = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CanDelete = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CanExport = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CanViewAnalytics = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MiddleName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DateOfBirth = table.Column<DateTime>(type: "date", nullable: true),
                    PhotoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PhoneNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    WhatsApp = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Telegram = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Instagram = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Messenger = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Viber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsEmployee = table.Column<bool>(type: "boolean", nullable: false),
                    EmploymentStartDate = table.Column<DateTime>(type: "date", nullable: true),
                    EmploymentEndDate = table.Column<DateTime>(type: "date", nullable: true),
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ApprovedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("CK_Users_Employment_Range", "(\"EmploymentStartDate\" IS NULL OR \"EmploymentEndDate\" IS NULL OR \"EmploymentStartDate\" <= \"EmploymentEndDate\")");
                    table.ForeignKey(
                        name: "FK_Users_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Users_Users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "LegalEntities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShortName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FullName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Inn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Kpp = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Ogrn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Phone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Director = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResponsibleUserId = table.Column<int>(type: "integer", nullable: true),
                    Country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BaseCurrency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ServiceLanguage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    OrganizationForm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TaxSystem = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TaxSystemMode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IncomeTaxRate = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    IncomeExpenseTaxRate = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    VatWithoutVat = table.Column<bool>(type: "boolean", nullable: true),
                    Okved = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Okpo = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Okato = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Oktmo = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Okogu = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Okfs = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Okopf = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Pfr = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Fss = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CharterCapital = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TaxRegistrationDate = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TaxOfficeCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    TaxRegion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IfnsName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TaxOfficePhone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TreasuryInn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TreasuryKpp = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TreasuryRecipientName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TreasuryBankBik = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    TreasuryBankName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TreasuryAccountNumber = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    TreasuryCorrAccountNumber = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    TreasuryKbk = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    BankAccountsJson = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    CashRegistersJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    CardsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    RequisitesEnglishJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    LogoDataUrl = table.Column<string>(type: "text", maxLength: 12000000, nullable: true),
                    SealDataUrl = table.Column<string>(type: "text", maxLength: 12000000, nullable: true),
                    DirectorSignatureDataUrl = table.Column<string>(type: "text", maxLength: 12000000, nullable: true),
                    DocumentDirectorJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ChiefAccountantJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ContactPhonesJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ContactEmailsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalEntities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegalEntities_Users_ResponsibleUserId",
                        column: x => x.ResponsibleUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UserActivityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    UserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UserFullName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Action = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Section = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ObjectType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ObjectId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    HttpMethod = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    QueryString = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserActivityLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserActivityLogs_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UserSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Company = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LegalEntityId = table.Column<int>(type: "integer", nullable: true),
                    ClientStatusId = table.Column<int>(type: "integer", nullable: true),
                    ResponsibleUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Clients_ClientStatuses_ClientStatusId",
                        column: x => x.ClientStatusId,
                        principalTable: "ClientStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Clients_LegalEntities_LegalEntityId",
                        column: x => x.LegalEntityId,
                        principalTable: "LegalEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Clients_Users_ResponsibleUserId",
                        column: x => x.ResponsibleUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AddressRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    UnrestrictedValue = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    PostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Country = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    RegionWithType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CityWithType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StreetWithType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    House = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Flat = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FiasId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    KladrId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    GeoLat = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    GeoLon = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    LegalEntityId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddressRecords", x => x.Id);
                    table.CheckConstraint("CK_AddressRecords_Owner", "(\"ClientId\" IS NOT NULL AND \"LegalEntityId\" IS NULL) OR (\"ClientId\" IS NULL AND \"LegalEntityId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AddressRecords_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AddressRecords_LegalEntities_LegalEntityId",
                        column: x => x.LegalEntityId,
                        principalTable: "LegalEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientCases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCases_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientContracts",
                columns: table => new
                {
                    ClientId = table.Column<int>(type: "integer", nullable: false),
                    ContractId = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientContracts", x => new { x.ContractId, x.ClientId });
                    table.ForeignKey(
                        name: "FK_ClientContracts_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientContracts_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientPassports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<int>(type: "integer", nullable: false),
                    Series = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IssuedBy = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DepartmentCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BirthPlacePassport = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BirthPlaceFias = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPassports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPassports_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PowersOfAttorney",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Issuer = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PowersOfAttorney", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PowersOfAttorney_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Account = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    AccountDate = table.Column<DateTime>(type: "date", nullable: true),
                    InvoiceId = table.Column<int>(type: "integer", nullable: true),
                    LegacyAccountNumber = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    LegacyAccountDate = table.Column<DateTime>(type: "date", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: true),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    ContractId = table.Column<int>(type: "integer", nullable: true),
                    DocumentKind = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    PlannedDate = table.Column<DateTime>(type: "date", nullable: false),
                    DealTypeId = table.Column<int>(type: "integer", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CounterpartyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IncomeTypeId = table.Column<int>(type: "integer", nullable: true),
                    IsPaid = table.Column<bool>(type: "boolean", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    LastPaymentDate = table.Column<DateTime>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ClientCaseReference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SystemNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    RescheduleCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    PaidDate = table.Column<DateTime>(type: "date", nullable: true),
                    PaymentSourceId = table.Column<int>(type: "integer", nullable: true),
                    ServiceId = table.Column<int>(type: "integer", nullable: true),
                    PaymentStatusId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Payments_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Payments_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Payments_DealTypes_DealTypeId",
                        column: x => x.DealTypeId,
                        principalTable: "DealTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Payments_IncomeTypes_IncomeTypeId",
                        column: x => x.IncomeTypeId,
                        principalTable: "IncomeTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Payments_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Payments_PaymentSources_PaymentSourceId",
                        column: x => x.PaymentSourceId,
                        principalTable: "PaymentSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Payments_PaymentStatuses_PaymentStatusId",
                        column: x => x.PaymentStatusId,
                        principalTable: "PaymentStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Payments_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Acts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InvoiceId = table.Column<int>(type: "integer", nullable: true),
                    CounterpartyInn = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    ResponsibleId = table.Column<int>(type: "integer", nullable: true),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Acts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Acts_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Acts_Payments_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Acts_Users_ResponsibleId",
                        column: x => x.ResponsibleId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ContractInvoices",
                columns: table => new
                {
                    ContractId = table.Column<int>(type: "integer", nullable: false),
                    InvoiceId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractInvoices", x => new { x.ContractId, x.InvoiceId });
                    table.ForeignKey(
                        name: "FK_ContractInvoices_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractInvoices_Payments_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContractPayments",
                columns: table => new
                {
                    ContractId = table.Column<int>(type: "integer", nullable: false),
                    PaymentId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractPayments", x => new { x.ContractId, x.PaymentId });
                    table.ForeignKey(
                        name: "FK_ContractPayments_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractPayments_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceClientCases",
                columns: table => new
                {
                    PaymentId = table.Column<int>(type: "integer", nullable: false),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceClientCases", x => new { x.PaymentId, x.ClientCaseId });
                    table.ForeignKey(
                        name: "FK_InvoiceClientCases_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvoiceClientCases_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentResponsibles",
                columns: table => new
                {
                    PaymentId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentResponsibles", x => new { x.PaymentId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PaymentResponsibles_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaymentResponsibles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContractActs",
                columns: table => new
                {
                    ContractId = table.Column<int>(type: "integer", nullable: false),
                    ActId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractActs", x => new { x.ContractId, x.ActId });
                    table.ForeignKey(
                        name: "FK_ContractActs_Acts_ActId",
                        column: x => x.ActId,
                        principalTable: "Acts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractActs_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Acts_ClientId",
                table: "Acts",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Acts_Date",
                table: "Acts",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Acts_InvoiceId",
                table: "Acts",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Acts_Number",
                table: "Acts",
                column: "Number");

            migrationBuilder.CreateIndex(
                name: "IX_Acts_ResponsibleId",
                table: "Acts",
                column: "ResponsibleId");

            migrationBuilder.CreateIndex(
                name: "IX_Acts_Status",
                table: "Acts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecords_ClientId",
                table: "AddressRecords",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecords_ClientId_Type_IsPrimary",
                table: "AddressRecords",
                columns: new[] { "ClientId", "Type", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecords_FiasId",
                table: "AddressRecords",
                column: "FiasId");

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecords_LegalEntityId",
                table: "AddressRecords",
                column: "LegalEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecords_LegalEntityId_Type_IsPrimary",
                table: "AddressRecords",
                columns: new[] { "LegalEntityId", "Type", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseCurrencyDictionaryItems_IsActive",
                table: "BaseCurrencies",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_BaseCurrencyDictionaryItems_IsActive_Name",
                table: "BaseCurrencies",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseCurrencyDictionaryItems_Name",
                table: "BaseCurrencies",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_ClientId_CreatedAt",
                table: "ClientCases",
                columns: new[] { "ClientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_ClientId_Status",
                table: "ClientCases",
                columns: new[] { "ClientId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_CreatedAt",
                table: "ClientCases",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_Status",
                table: "ClientCases",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ClientContracts_ClientId",
                table: "ClientContracts",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassports_ClientId",
                table: "ClientPassports",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassports_ClientId_IsPrimary",
                table: "ClientPassports",
                columns: new[] { "ClientId", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassports_ClientId_Series_Number",
                table: "ClientPassports",
                columns: new[] { "ClientId", "Series", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_ClientStatusId",
                table: "Clients",
                column: "ClientStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_CreatedAt",
                table: "Clients",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_Email",
                table: "Clients",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_IsActive",
                table: "Clients",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_IsActive_Name",
                table: "Clients",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_LegalEntityId",
                table: "Clients",
                column: "LegalEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_Name",
                table: "Clients",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_ResponsibleUserId",
                table: "Clients",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientStatuses_IsActive",
                table: "ClientStatuses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ClientStatuses_IsActive_Name",
                table: "ClientStatuses",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientStatuses_Name",
                table: "ClientStatuses",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ContractActs_ActId",
                table: "ContractActs",
                column: "ActId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractInvoices_InvoiceId",
                table: "ContractInvoices",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractPayments_PaymentId",
                table: "ContractPayments",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_Date",
                table: "Contracts",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_Number",
                table: "Contracts",
                column: "Number");

            migrationBuilder.CreateIndex(
                name: "IX_CountryDictionaryItems_IsActive",
                table: "Countries",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CountryDictionaryItems_IsActive_Name",
                table: "Countries",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_CountryDictionaryItems_Name",
                table: "Countries",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_DealTypes_IsActive",
                table: "DealTypes",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_DealTypes_IsActive_Name",
                table: "DealTypes",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_DealTypes_Name",
                table: "DealTypes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_FormFieldRequirements_Section_FieldKey",
                table: "FormFieldRequirements",
                columns: new[] { "Section", "FieldKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncomeTypes_IsActive",
                table: "IncomeTypes",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_IncomeTypes_IsActive_Name",
                table: "IncomeTypes",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_IncomeTypes_IsActive_PaymentType",
                table: "IncomeTypes",
                columns: new[] { "IsActive", "PaymentType" });

            migrationBuilder.CreateIndex(
                name: "IX_IncomeTypes_IsActive_PaymentType_Name",
                table: "IncomeTypes",
                columns: new[] { "IsActive", "PaymentType", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_IncomeTypes_Name",
                table: "IncomeTypes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceClientCases_ClientCaseId",
                table: "InvoiceClientCases",
                column: "ClientCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItemNameDictionaryItems_IsActive",
                table: "InvoiceItemNames",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItemNameDictionaryItems_IsActive_Name",
                table: "InvoiceItemNames",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItemNameDictionaryItems_Name",
                table: "InvoiceItemNames",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Date",
                table: "Invoices",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Number",
                table: "Invoices",
                column: "Number");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Number_Date",
                table: "Invoices",
                columns: new[] { "Number", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceUnitDictionaryItems_IsActive",
                table: "InvoiceUnits",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceUnitDictionaryItems_IsActive_Name",
                table: "InvoiceUnits",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceUnitDictionaryItems_Name",
                table: "InvoiceUnits",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntities_Inn",
                table: "LegalEntities",
                column: "Inn");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntities_Kpp",
                table: "LegalEntities",
                column: "Kpp");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntities_ResponsibleUserId",
                table: "LegalEntities",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntities_ShortName",
                table: "LegalEntities",
                column: "ShortName");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_Date",
                table: "MoneyOperations",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_Direction",
                table: "MoneyOperations",
                column: "Direction");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_Source",
                table: "MoneyOperations",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_Source_Date",
                table: "MoneyOperations",
                columns: new[] { "Source", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_OkinDictionaryItems_Code",
                table: "OkinDictionaryItems",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OkinDictionaryItems_IsActive_Name",
                table: "OkinDictionaryItems",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_OkpdtrDictionaryItems_Code",
                table: "OkpdtrDictionaryItems",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OkpdtrDictionaryItems_IsActive_Name",
                table: "OkpdtrDictionaryItems",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_OksoDictionaryItems_Code",
                table: "OksoDictionaryItems",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OksoDictionaryItems_IsActive_Name",
                table: "OksoDictionaryItems",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationFormDictionaryItems_IsActive",
                table: "OrganizationForms",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationFormDictionaryItems_IsActive_Name",
                table: "OrganizationForms",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationFormDictionaryItems_Name",
                table: "OrganizationForms",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentResponsibles_UserId",
                table: "PaymentResponsibles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Account",
                table: "Payments",
                column: "Account");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Account_AccountDate",
                table: "Payments",
                columns: new[] { "Account", "AccountDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_AccountDate",
                table: "Payments",
                column: "AccountDate");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ClientCaseId_Date",
                table: "Payments",
                columns: new[] { "ClientCaseId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ClientCaseId_Date_Type_Status",
                table: "Payments",
                columns: new[] { "ClientCaseId", "Date", "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ClientId_Date",
                table: "Payments",
                columns: new[] { "ClientId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ClientId_Date_Type_Status",
                table: "Payments",
                columns: new[] { "ClientId", "Date", "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ContractId",
                table: "Payments",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CreatedAt",
                table: "Payments",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Date",
                table: "Payments",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_DealTypeId",
                table: "Payments",
                column: "DealTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_DocumentKind",
                table: "Payments",
                column: "DocumentKind");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IncomeTypeId",
                table: "Payments",
                column: "IncomeTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_InvoiceId",
                table: "Payments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IsPaid_Date",
                table: "Payments",
                columns: new[] { "IsPaid", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IsPaid_Status",
                table: "Payments",
                columns: new[] { "IsPaid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LastPaymentDate",
                table: "Payments",
                column: "LastPaymentDate");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LegacyAccount_LegacyAccountDate",
                table: "Payments",
                columns: new[] { "LegacyAccountNumber", "LegacyAccountDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LegacyAccountDate",
                table: "Payments",
                column: "LegacyAccountDate");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LegacyAccountNumber",
                table: "Payments",
                column: "LegacyAccountNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaymentSourceId",
                table: "Payments",
                column: "PaymentSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaymentStatusId",
                table: "Payments",
                column: "PaymentStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PlannedDate",
                table: "Payments",
                column: "PlannedDate");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ServiceId",
                table: "Payments",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Type_Status_Date",
                table: "Payments",
                columns: new[] { "Type", "Status", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSources_IsActive",
                table: "PaymentSources",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSources_IsActive_Name",
                table: "PaymentSources",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSources_Name",
                table: "PaymentSources",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentStatuses_IsActive",
                table: "PaymentStatuses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentStatuses_IsActive_Name",
                table: "PaymentStatuses",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentStatuses_Name",
                table: "PaymentStatuses",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_PowersOfAttorney_ClientId_IsActive",
                table: "PowersOfAttorney",
                columns: new[] { "ClientId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_PowersOfAttorney_Number",
                table: "PowersOfAttorney",
                column: "Number");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_Role_Section",
                table: "RolePermissions",
                columns: new[] { "RoleId", "Section" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLanguageDictionaryItems_IsActive",
                table: "ServiceLanguages",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLanguageDictionaryItems_IsActive_Name",
                table: "ServiceLanguages",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLanguageDictionaryItems_Name",
                table: "ServiceLanguages",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDictionaryItems_IsActive",
                table: "Services",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDictionaryItems_IsActive_Name",
                table: "Services",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDictionaryItems_Name",
                table: "Services",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_Key",
                table: "SystemSettings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemModeDictionaryItems_IsActive",
                table: "TaxSystemModes",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemModeDictionaryItems_IsActive_Name",
                table: "TaxSystemModes",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemModeDictionaryItems_Name",
                table: "TaxSystemModes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemDictionaryItems_IsActive",
                table: "TaxSystems",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemDictionaryItems_IsActive_Name",
                table: "TaxSystems",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxSystemDictionaryItems_Name",
                table: "TaxSystems",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_TimezoneDictionaryItems_IsActive",
                table: "Timezones",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TimezoneDictionaryItems_IsActive_Name",
                table: "Timezones",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_TimezoneDictionaryItems_Name",
                table: "Timezones",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityLogs_Action",
                table: "UserActivityLogs",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityLogs_Category",
                table: "UserActivityLogs",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityLogs_CreatedAt",
                table: "UserActivityLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityLogs_Status",
                table: "UserActivityLogs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityLogs_UserId",
                table: "UserActivityLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "idx_users_active",
                table: "Users",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "idx_users_approval",
                table: "Users",
                column: "IsApproved");

            migrationBuilder.CreateIndex(
                name: "idx_users_approvedAt",
                table: "Users",
                column: "ApprovedAt");

            migrationBuilder.CreateIndex(
                name: "idx_users_created",
                table: "Users",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "idx_users_employment_range",
                table: "Users",
                columns: new[] { "EmploymentStartDate", "EmploymentEndDate" });

            migrationBuilder.CreateIndex(
                name: "idx_users_isemployee",
                table: "Users",
                column: "IsEmployee");

            migrationBuilder.CreateIndex(
                name: "idx_users_role",
                table: "Users",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "idx_users_updated",
                table: "Users",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ApprovedByUserId",
                table: "Users",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSettings_User_Key",
                table: "UserSettings",
                columns: new[] { "UserId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AddressRecords");

            migrationBuilder.DropTable(
                name: "BaseCurrencies");

            migrationBuilder.DropTable(
                name: "ClientContracts");

            migrationBuilder.DropTable(
                name: "ClientPassports");

            migrationBuilder.DropTable(
                name: "ContractActs");

            migrationBuilder.DropTable(
                name: "ContractInvoices");

            migrationBuilder.DropTable(
                name: "ContractPayments");

            migrationBuilder.DropTable(
                name: "Countries");

            migrationBuilder.DropTable(
                name: "FormFieldRequirements");

            migrationBuilder.DropTable(
                name: "InvoiceClientCases");

            migrationBuilder.DropTable(
                name: "InvoiceItemNames");

            migrationBuilder.DropTable(
                name: "InvoiceUnits");

            migrationBuilder.DropTable(
                name: "MoneyOperations");

            migrationBuilder.DropTable(
                name: "OkinDictionaryItems");

            migrationBuilder.DropTable(
                name: "OkpdtrDictionaryItems");

            migrationBuilder.DropTable(
                name: "OksoDictionaryItems");

            migrationBuilder.DropTable(
                name: "OrganizationForms");

            migrationBuilder.DropTable(
                name: "PaymentResponsibles");

            migrationBuilder.DropTable(
                name: "PowersOfAttorney");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "ServiceLanguages");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "TaxSystemModes");

            migrationBuilder.DropTable(
                name: "TaxSystems");

            migrationBuilder.DropTable(
                name: "Timezones");

            migrationBuilder.DropTable(
                name: "UserActivityLogs");

            migrationBuilder.DropTable(
                name: "UserSettings");

            migrationBuilder.DropTable(
                name: "Acts");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "ClientCases");

            migrationBuilder.DropTable(
                name: "Contracts");

            migrationBuilder.DropTable(
                name: "DealTypes");

            migrationBuilder.DropTable(
                name: "IncomeTypes");

            migrationBuilder.DropTable(
                name: "Invoices");

            migrationBuilder.DropTable(
                name: "PaymentSources");

            migrationBuilder.DropTable(
                name: "PaymentStatuses");

            migrationBuilder.DropTable(
                name: "Services");

            migrationBuilder.DropTable(
                name: "Clients");

            migrationBuilder.DropTable(
                name: "ClientStatuses");

            migrationBuilder.DropTable(
                name: "LegalEntities");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Roles");
        }
    }
}
