using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLightningSwapMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LightningSwaps",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    SwapId = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    SwapType = table.Column<string>(type: "text", nullable: false),
                    Invoice = table.Column<string>(type: "text", nullable: false),
                    LockupAddress = table.Column<string>(type: "text", nullable: false),
                    OnchainAmount = table.Column<long>(type: "bigint", nullable: false),
                    TimeoutBlockHeight = table.Column<long>(type: "bigint", nullable: false),
                    PreimageHash = table.Column<string>(type: "text", nullable: true),
                    ClaimAddress = table.Column<string>(type: "text", nullable: true),
                    ContractScript = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false, defaultValue: "created"),
                    TransactionId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsInvoiceReturned = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LightningSwaps", x => x.SwapId);
                    table.ForeignKey(
                        name: "FK_LightningSwaps_WalletContracts_ContractScript_WalletId",
                        columns: x => new { x.ContractScript, x.WalletId },
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "WalletContracts",
                        principalColumns: new[] { "Script", "WalletId" },
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LightningSwaps_ContractScript_WalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "LightningSwaps",
                columns: new[] { "ContractScript", "WalletId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LightningSwaps",
                schema: "BTCPayServer.Plugins.Ark");
        }
    }
}
