using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReworkArkEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WalletContracts_Wallets_DescriptorTemplate",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Wallets",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            migrationBuilder.DropIndex(
                name: "IX_WalletContracts_DescriptorTemplate",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.RenameColumn(
                name: "DescriptorTemplate",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                newName: "PubKey");

            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

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

            migrationBuilder.AddColumn<bool>(
                name: "IsNote",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Preconfirmed",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Wallets",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                column: "Id");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WalletContracts_Wallets_ArkWalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Wallets",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            migrationBuilder.DropIndex(
                name: "IX_WalletContracts_ArkWalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.DropColumn(
                name: "Id",
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

            migrationBuilder.DropColumn(
                name: "IsNote",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos");

            migrationBuilder.DropColumn(
                name: "Preconfirmed",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos");

            migrationBuilder.RenameColumn(
                name: "PubKey",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                newName: "DescriptorTemplate");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Wallets",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                column: "DescriptorTemplate");

            migrationBuilder.CreateIndex(
                name: "IX_WalletContracts_DescriptorTemplate",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                column: "DescriptorTemplate");

            migrationBuilder.AddForeignKey(
                name: "FK_WalletContracts_Wallets_DescriptorTemplate",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                column: "DescriptorTemplate",
                principalSchema: "BTCPayServer.Plugins.Ark",
                principalTable: "Wallets",
                principalColumn: "DescriptorTemplate",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
