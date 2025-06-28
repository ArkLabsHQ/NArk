using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateWalletEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WalletContracts_Wallets_ArkWalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.DropIndex(
                name: "IX_WalletContracts_ArkWalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.DropColumn(
                name: "CurrentIndex",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "EncryptedPrvkey",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "ArkWalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.AddColumn<string>(
                name: "Wallet",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "WalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_WalletContracts_WalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                column: "WalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_WalletContracts_Wallets_WalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                column: "WalletId",
                principalSchema: "BTCPayServer.Plugins.Ark",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WalletContracts_Wallets_WalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.DropIndex(
                name: "IX_WalletContracts_WalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.DropColumn(
                name: "Wallet",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "WalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.AddColumn<long>(
                name: "CurrentIndex",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptedPrvkey",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "PasswordHash",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<Guid>(
                name: "ArkWalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletContracts_ArkWalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                column: "ArkWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_WalletContracts_Wallets_ArkWalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                column: "ArkWalletId",
                principalSchema: "BTCPayServer.Plugins.Ark",
                principalTable: "Wallets",
                principalColumn: "Id");
        }
    }
}
