using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobScout.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppConfig",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    MinimumSalary = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Cities = table.Column<string>(type: "TEXT", nullable: false),
                    IncludeRemote = table.Column<bool>(type: "INTEGER", nullable: false),
                    CvFileName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CvBlob = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CvText = table.Column<string>(type: "TEXT", nullable: true),
                    CvUploadedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppConfig", x => x.Id);
                    table.CheckConstraint("CK_AppConfig_SingleRow", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    NormalisedName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    BoardUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DiscoveredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailCheckpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MailboxKey = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    LastCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UidValidity = table.Column<uint>(type: "INTEGER", nullable: true),
                    LastUid = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailCheckpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Industries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Industries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobRunLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRunLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobListings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    SalaryMin = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    SalaryMax = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    SalaryCurrency = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    SalaryKnown = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FoundAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PostedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Score = table.Column<int>(type: "INTEGER", nullable: true),
                    Reasoning = table.Column<string>(type: "TEXT", nullable: true),
                    Strengths = table.Column<string>(type: "TEXT", nullable: true),
                    Gaps = table.Column<string>(type: "TEXT", nullable: true),
                    ScoredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ScoredContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsRemote = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobListings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobListings_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Applications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobListingId = table.Column<int>(type: "INTEGER", nullable: true),
                    CompanyName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AppliedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LastEmailAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Applications_JobListings_JobListingId",
                        column: x => x.JobListingId,
                        principalTable: "JobListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationStatusChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobApplicationId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ToStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationStatusChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationStatusChanges_Applications_JobApplicationId",
                        column: x => x.JobApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SuggestedStatusUpdates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobApplicationId = table.Column<int>(type: "INTEGER", nullable: false),
                    SuggestedStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Rationale = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    EmailFrom = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    EmailSubject = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    EmailReceivedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsResolved = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuggestedStatusUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SuggestedStatusUpdates_Applications_JobApplicationId",
                        column: x => x.JobApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_AppliedAt",
                table: "Applications",
                column: "AppliedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Applications_JobListingId",
                table: "Applications",
                column: "JobListingId");

            migrationBuilder.CreateIndex(
                name: "IX_Applications_Status",
                table: "Applications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationStatusChanges_JobApplicationId",
                table: "ApplicationStatusChanges",
                column: "JobApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_NormalisedName",
                table: "Companies",
                column: "NormalisedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_Status",
                table: "Companies",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_EmailCheckpoints_MailboxKey",
                table: "EmailCheckpoints",
                column: "MailboxKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Industries_Name",
                table: "Industries",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobListings_CompanyId",
                table: "JobListings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_JobListings_FoundAt",
                table: "JobListings",
                column: "FoundAt");

            migrationBuilder.CreateIndex(
                name: "IX_JobListings_Score",
                table: "JobListings",
                column: "Score");

            migrationBuilder.CreateIndex(
                name: "IX_JobListings_Status",
                table: "JobListings",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_JobListings_Url",
                table: "JobListings",
                column: "Url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRunLogs_JobName_StartedAt",
                table: "JobRunLogs",
                columns: new[] { "JobName", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedStatusUpdates_IsResolved",
                table: "SuggestedStatusUpdates",
                column: "IsResolved");

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedStatusUpdates_JobApplicationId",
                table: "SuggestedStatusUpdates",
                column: "JobApplicationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppConfig");

            migrationBuilder.DropTable(
                name: "ApplicationStatusChanges");

            migrationBuilder.DropTable(
                name: "EmailCheckpoints");

            migrationBuilder.DropTable(
                name: "Industries");

            migrationBuilder.DropTable(
                name: "JobRunLogs");

            migrationBuilder.DropTable(
                name: "SuggestedStatusUpdates");

            migrationBuilder.DropTable(
                name: "Applications");

            migrationBuilder.DropTable(
                name: "JobListings");

            migrationBuilder.DropTable(
                name: "Companies");
        }
    }
}
