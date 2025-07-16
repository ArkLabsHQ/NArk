using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class fixkey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_LightningSwaps",
                schema: "BTCPayServer.Plugins.Ark",
                table: "LightningSwaps");

            migrationBuilder.DropColumn(
                name: "IsInvoiceReturned",
                schema: "BTCPayServer.Plugins.Ark",
                table: "LightningSwaps");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                schema: "BTCPayServer.Plugins.Ark",
                table: "LightningSwaps",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "created");

            migrationBuilder.AddPrimaryKey(
                name: "PK_LightningSwaps",
                schema: "BTCPayServer.Plugins.Ark",
                table: "LightningSwaps",
                columns: new[] { "SwapId", "WalletId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_LightningSwaps",
                schema: "BTCPayServer.Plugins.Ark",
                table: "LightningSwaps");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                schema: "BTCPayServer.Plugins.Ark",
                table: "LightningSwaps",
                type: "text",
                nullable: false,
                defaultValue: "created",
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<bool>(
                name: "IsInvoiceReturned",
                schema: "BTCPayServer.Plugins.Ark",
                table: "LightningSwaps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddPrimaryKey(
                name: "PK_LightningSwaps",
                schema: "BTCPayServer.Plugins.Ark",
                table: "LightningSwaps",
                column: "SwapId");
        }
    }
}
