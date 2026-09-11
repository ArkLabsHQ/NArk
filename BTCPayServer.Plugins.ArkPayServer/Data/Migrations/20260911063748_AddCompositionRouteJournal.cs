using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositionRouteJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BaseAmountSats",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvmAmount",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(78)",
                maxLength: 78,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvmClaimAddress",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(42)",
                maxLength: 42,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvmClaimTransactionId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(66)",
                maxLength: 66,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvmLockTransactionId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(66)",
                maxLength: 66,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvmObservedAtBlock",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(78)",
                maxLength: 78,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvmProvenAtBlock",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(78)",
                maxLength: 78,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EvmProvenBlockTimestamp",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvmRefundAddress",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(42)",
                maxLength: 42,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvmTimeoutBlock",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(78)",
                maxLength: 78,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvmTokenAddress",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(42)",
                maxLength: 42,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IngressClaimTransactionId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "SwapContractAddress",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(42)",
                maxLength: 42,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InvoiceCompositionLegs",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    RfqId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RouteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SolverPubkey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FromAmount = table.Column<string>(type: "character varying(78)", maxLength: 78, nullable: true),
                    ToAmount = table.Column<string>(type: "character varying(78)", maxLength: 78, nullable: true),
                    LockupScript = table.Column<string>(type: "character varying(68)", maxLength: 68, nullable: true),
                    LockupAddress = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PayoutScript = table.Column<string>(type: "character varying(68)", maxLength: 68, nullable: true),
                    ValidUntil = table.Column<long>(type: "bigint", nullable: true),
                    RefundLocktime = table.Column<long>(type: "bigint", nullable: true),
                    FundingTransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FundedAmountSats = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceCompositionLegs", x => x.RfqId);
                    table.ForeignKey(
                        name: "FK_InvoiceCompositionLegs_InvoiceCompositions_RouteId",
                        column: x => x.RouteId,
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "InvoiceCompositions",
                        principalColumn: "RouteId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceCompositionLegs_RouteId_Kind",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositionLegs",
                columns: new[] { "RouteId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceCompositionLegs",
                schema: "BTCPayServer.Plugins.Ark");

            migrationBuilder.DropColumn(
                name: "BaseAmountSats",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "EvmAmount",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "EvmClaimAddress",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "EvmClaimTransactionId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "EvmLockTransactionId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "EvmObservedAtBlock",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "EvmProvenAtBlock",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "EvmProvenBlockTimestamp",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "EvmRefundAddress",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "EvmTimeoutBlock",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "EvmTokenAddress",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "IngressClaimTransactionId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "Revision",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "SwapContractAddress",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");
        }
    }
}
