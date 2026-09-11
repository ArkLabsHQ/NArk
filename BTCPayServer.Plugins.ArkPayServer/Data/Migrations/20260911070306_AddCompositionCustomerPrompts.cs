using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositionCustomerPrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CheckoutExpiresAt",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerDestination",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckoutExpiresAt",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");

            migrationBuilder.DropColumn(
                name: "CustomerDestination",
                schema: "BTCPayServer.Plugins.Ark",
                table: "InvoiceCompositions");
        }
    }
}
