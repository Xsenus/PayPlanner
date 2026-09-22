using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StorageObjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DataClass = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    LogicalName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    CurrentGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageObjects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageObjectGenerations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageObjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Generation = table.Column<long>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PolicyVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageObjectGenerations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageObjectGenerations_StorageObjects_StorageObjectId",
                        column: x => x.StorageObjectId,
                        principalTable: "StorageObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StorageTombstones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageObjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Generation = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LegalHold = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetainUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedByUserId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageTombstones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageTombstones_StorageObjects_StorageObjectId",
                        column: x => x.StorageObjectId,
                        principalTable: "StorageObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StorageOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageObjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageObjectGenerationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OperationType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageOperations_StorageObjectGenerations_StorageObjectGenerationId",
                        column: x => x.StorageObjectGenerationId,
                        principalTable: "StorageObjectGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StorageOperations_StorageObjects_StorageObjectId",
                        column: x => x.StorageObjectId,
                        principalTable: "StorageObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StorageReplicas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StorageObjectGenerationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Locator = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ProviderVersion = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    LastVerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageReplicas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageReplicas_StorageObjectGenerations_StorageObjectGenerationId",
                        column: x => x.StorageObjectGenerationId,
                        principalTable: "StorageObjectGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageObjectGenerations_StorageObjectId_Generation",
                table: "StorageObjectGenerations",
                columns: new[] { "StorageObjectId", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageObjects_DataClass_IsDeleted_UpdatedAt",
                table: "StorageObjects",
                columns: new[] { "DataClass", "IsDeleted", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageObjects_OwnerType_OwnerId_DataClass",
                table: "StorageObjects",
                columns: new[] { "OwnerType", "OwnerId", "DataClass" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageOperations_IdempotencyKey",
                table: "StorageOperations",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageOperations_ProviderKey_Status_NextAttemptAt",
                table: "StorageOperations",
                columns: new[] { "ProviderKey", "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageOperations_Status_NextAttemptAt",
                table: "StorageOperations",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageOperations_StorageObjectGenerationId",
                table: "StorageOperations",
                column: "StorageObjectGenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageOperations_StorageObjectId",
                table: "StorageOperations",
                column: "StorageObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageReplicas_ProviderKey_ConnectionId_Status",
                table: "StorageReplicas",
                columns: new[] { "ProviderKey", "ConnectionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageReplicas_StorageObjectGenerationId_ProviderKey_ConnectionId_Locator",
                table: "StorageReplicas",
                columns: new[] { "StorageObjectGenerationId", "ProviderKey", "ConnectionId", "Locator" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageReplicas_StorageObjectGenerationId_Status",
                table: "StorageReplicas",
                columns: new[] { "StorageObjectGenerationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageTombstones_LegalHold_RetainUntil",
                table: "StorageTombstones",
                columns: new[] { "LegalHold", "RetainUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageTombstones_StorageObjectId_Generation",
                table: "StorageTombstones",
                columns: new[] { "StorageObjectId", "Generation" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageOperations");

            migrationBuilder.DropTable(
                name: "StorageReplicas");

            migrationBuilder.DropTable(
                name: "StorageTombstones");

            migrationBuilder.DropTable(
                name: "StorageObjectGenerations");

            migrationBuilder.DropTable(
                name: "StorageObjects");
        }
    }
}
