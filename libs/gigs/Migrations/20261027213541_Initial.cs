using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Encore.Gigs.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "gigs");

            migrationBuilder.CreateTable(
                name: "Gigs",
                schema: "gigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Stage = table.Column<string>(type: "text", nullable: false),
                    Band = table.Column<string>(type: "text", nullable: false),
                    GigDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PriceCents = table.Column<long>(type: "bigint", nullable: false),
                    SeatsTotal = table.Column<int>(type: "integer", nullable: false),
                    SeatsLeft = table.Column<int>(type: "integer", nullable: false),
                    MaxPerFan = table.Column<int>(type: "integer", nullable: false, defaultValue: 2),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Gigs", x => x.Id);
                    table.CheckConstraint("CK_Gigs_Cap", "\"MaxPerFan\" >= 1");
                    table.CheckConstraint("CK_Gigs_Remaining", "\"SeatsLeft\" >= 0 AND \"SeatsLeft\" <= \"SeatsTotal\"");
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "gigs",
                columns: table => new
                {
                    Consumer = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "gigs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: false),
                    RoutingKey = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GigFans",
                schema: "gigs",
                columns: table => new
                {
                    GigId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seats = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GigFans", x => new { x.GigId, x.OwnerId });
                    table.CheckConstraint("CK_GigFans_Seats", "\"Seats\" >= 0");
                    table.ForeignKey(
                        name: "FK_GigFans_Gigs_GigId",
                        column: x => x.GigId,
                        principalSchema: "gigs",
                        principalTable: "Gigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeatHolds",
                schema: "gigs",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    GigId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seats = table.Column<int>(type: "integer", nullable: false),
                    SubtotalCents = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeatHolds", x => x.OrderId);
                    table.ForeignKey(
                        name: "FK_SeatHolds_Gigs_GigId",
                        column: x => x.GigId,
                        principalSchema: "gigs",
                        principalTable: "Gigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Gigs_Code",
                schema: "gigs",
                table: "Gigs",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SentAt",
                schema: "gigs",
                table: "OutboxMessages",
                column: "SentAt",
                filter: "\"SentAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SeatHolds_GigId",
                schema: "gigs",
                table: "SeatHolds",
                column: "GigId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GigFans",
                schema: "gigs");

            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "gigs");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "gigs");

            migrationBuilder.DropTable(
                name: "SeatHolds",
                schema: "gigs");

            migrationBuilder.DropTable(
                name: "Gigs",
                schema: "gigs");
        }
    }
}
