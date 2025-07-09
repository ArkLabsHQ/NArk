using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "BTCPayServer.Plugins.Ark");

            migrationBuilder.CreateTable(
                name: "Vtxos",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    TransactionOutputIndex = table.Column<int>(type: "integer", nullable: false),
                    Script = table.Column<string>(type: "text", nullable: false),
                    SpentByTransactionId = table.Column<string>(type: "text", nullable: true),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    SeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsNote = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vtxos", x => new { x.TransactionId, x.TransactionOutputIndex });
                });

            migrationBuilder.CreateTable(
                name: "Wallets",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Wallet = table.Column<string>(type: "text", nullable: false),
                    WalletData = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WalletContracts",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    Script = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    ContractData = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletContracts", x => new { x.Script, x.WalletId });
                    table.ForeignKey(
                        name: "FK_WalletContracts_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletContracts_WalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_Wallet",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                column: "Wallet",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Vtxos",
                schema: "BTCPayServer.Plugins.Ark");

            migrationBuilder.DropTable(
                name: "WalletContracts",
                schema: "BTCPayServer.Plugins.Ark");

            migrationBuilder.DropTable(
                name: "Wallets",
                schema: "BTCPayServer.Plugins.Ark");
        }
    }
}
