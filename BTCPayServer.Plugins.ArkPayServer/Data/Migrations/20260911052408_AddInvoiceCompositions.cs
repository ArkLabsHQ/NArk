using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceCompositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvoiceCompositions",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    InvoiceId = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    AssetId = table.Column<string>(type: "character varying(88)", maxLength: 88, nullable: false),
                    Destination = table.Column<string>(type: "character varying(42)", maxLength: 42, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceCompositions", x => new { x.StoreId, x.InvoiceId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceCompositions",
                schema: "BTCPayServer.Plugins.Ark");
        }
    }
}
