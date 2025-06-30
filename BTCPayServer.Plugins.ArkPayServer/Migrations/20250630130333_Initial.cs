using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Migrations
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
                name: "Transactions",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    Psbt = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.TransactionId);
                });

            migrationBuilder.CreateTable(
                name: "Wallets",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Wallet = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vtxos",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    TransactionOutputIndex = table.Column<int>(type: "integer", nullable: false),
                    SpentByTransactionId = table.Column<string>(type: "text", nullable: true),
                    SpentByTransactionIdInputIndex = table.Column<int>(type: "integer", nullable: true),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    SeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SpentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsNote = table.Column<bool>(type: "boolean", nullable: false),
                    Preconfirmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vtxos", x => new { x.TransactionId, x.TransactionOutputIndex });
                    table.ForeignKey(
                        name: "FK_Vtxos_Transactions_SpentByTransactionId",
                        column: x => x.SpentByTransactionId,
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "Transactions",
                        principalColumn: "TransactionId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Vtxos_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "Transactions",
                        principalColumn: "TransactionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WalletContracts",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    Script = table.Column<string>(type: "text", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    ContractData = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    VTXOTransactionId = table.Column<string>(type: "text", nullable: true),
                    VTXOTransactionOutputIndex = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletContracts", x => x.Script);
                    table.ForeignKey(
                        name: "FK_WalletContracts_Vtxos_VTXOTransactionId_VTXOTransactionOutp~",
                        columns: x => new { x.VTXOTransactionId, x.VTXOTransactionOutputIndex },
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "Vtxos",
                        principalColumns: new[] { "TransactionId", "TransactionOutputIndex" });
                    table.ForeignKey(
                        name: "FK_WalletContracts_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Vtxos_SpentByTransactionId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                column: "SpentByTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletContracts_VTXOTransactionId_VTXOTransactionOutputIndex",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                columns: new[] { "VTXOTransactionId", "VTXOTransactionOutputIndex" });

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
                name: "WalletContracts",
                schema: "BTCPayServer.Plugins.Ark");

            migrationBuilder.DropTable(
                name: "Vtxos",
                schema: "BTCPayServer.Plugins.Ark");

            migrationBuilder.DropTable(
                name: "Wallets",
                schema: "BTCPayServer.Plugins.Ark");

            migrationBuilder.DropTable(
                name: "Transactions",
                schema: "BTCPayServer.Plugins.Ark");
        }
    }
}
