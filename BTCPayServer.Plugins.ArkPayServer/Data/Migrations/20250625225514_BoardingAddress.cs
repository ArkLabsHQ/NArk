using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class BoardingAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BoardingAddresses",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    OnchainAddress = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    DerivationIndex = table.Column<long>(type: "bigint", nullable: false),
                    BoardingExitDelay = table.Column<long>(type: "bigint", nullable: false),
                    ContractData = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardingAddresses", x => x.OnchainAddress);
                    table.ForeignKey(
                        name: "FK_BoardingAddresses_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BoardingAddresses_WalletId_DerivationIndex",
                schema: "BTCPayServer.Plugins.Ark",
                table: "BoardingAddresses",
                columns: new[] { "WalletId", "DerivationIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BoardingAddresses",
                schema: "BTCPayServer.Plugins.Ark");
        }
    }
}
