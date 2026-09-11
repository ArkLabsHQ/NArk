using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlignPinnedSdkModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "HtlcLocktime",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HtlcPubkey",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OnchainPayoutAddress",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HtlcLocktime",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents");

            migrationBuilder.DropColumn(
                name: "HtlcPubkey",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents");

            migrationBuilder.DropColumn(
                name: "OnchainPayoutAddress",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents");
        }
    }
}
