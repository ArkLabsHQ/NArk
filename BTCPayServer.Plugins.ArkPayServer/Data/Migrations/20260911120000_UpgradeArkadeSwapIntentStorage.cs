using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    public partial class UpgradeArkadeSwapIntentStorage : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql(
                    """
                    UPDATE "ArkadeSwapIntents"
                    SET "Metadata" = (
                        SELECT json_group_object("key", "value")
                        FROM (
                            SELECT 'offerHex' AS "key", "OfferHex" AS "value"
                            UNION ALL SELECT 'makerDescriptor', "MakerDescriptor"
                            UNION ALL SELECT 'invoice', "Invoice"
                            UNION ALL SELECT 'preimage', "Preimage"
                            UNION ALL SELECT 'htlcPubkey', "HtlcPubkey"
                            UNION ALL SELECT 'htlcLocktime', CAST("HtlcLocktime" AS TEXT)
                            UNION ALL SELECT 'onchainPayoutAddress', "OnchainPayoutAddress"
                        ) AS "LegacyMetadata"
                        WHERE "value" IS NOT NULL
                    );

                    CREATE TABLE "__temp_ArkadeSwapIntents" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ArkadeSwapIntents" PRIMARY KEY,
                        "WalletId" TEXT NOT NULL,
                        "Type" TEXT NOT NULL,
                        "OfferAmount" INTEGER NOT NULL,
                        "WantAmount" INTEGER NOT NULL,
                        "Status" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "SwapPkScript" TEXT NOT NULL,
                        "SwapAddress" TEXT NOT NULL,
                        "OfferHex" TEXT NULL,
                        "MakerDescriptor" TEXT NULL,
                        "FromAssetId" TEXT NULL,
                        "ToAssetId" TEXT NULL,
                        "Invoice" TEXT NULL,
                        "PaymentHash" TEXT NULL,
                        "RefundLocktime" INTEGER NULL,
                        "Preimage" TEXT NULL,
                        "SpentTxid" TEXT NULL,
                        "HtlcLocktime" INTEGER NULL,
                        "HtlcPubkey" TEXT NULL,
                        "OnchainPayoutAddress" TEXT NULL,
                        "Metadata" TEXT NOT NULL DEFAULT '{}'
                    );

                    INSERT INTO "__temp_ArkadeSwapIntents" (
                        "Id", "WalletId", "Type", "OfferAmount", "WantAmount", "Status",
                        "CreatedAt", "SwapPkScript", "SwapAddress", "OfferHex", "MakerDescriptor",
                        "FromAssetId", "ToAssetId", "Invoice", "PaymentHash", "RefundLocktime",
                        "Preimage", "SpentTxid", "HtlcLocktime", "HtlcPubkey",
                        "OnchainPayoutAddress", "Metadata")
                    SELECT
                        "Id", "WalletId",
                        CASE "Type"
                            WHEN 0 THEN 'BtcToAsset'
                            WHEN 1 THEN 'AssetToBtc'
                            WHEN 2 THEN 'BtcToLightning'
                            WHEN 3 THEN 'LightningToBtc'
                            WHEN 4 THEN 'BtcToOnchain'
                            ELSE CAST("Type" AS TEXT)
                        END,
                        "OfferAmount", "WantAmount",
                        CASE "Status"
                            WHEN 0 THEN 'Funding'
                            WHEN 1 THEN 'Pending'
                            WHEN 2 THEN 'Claimable'
                            WHEN 3 THEN 'Cancelling'
                            WHEN 4 THEN 'Fulfilled'
                            WHEN 5 THEN 'Cancelled'
                            WHEN 6 THEN 'Recoverable'
                            WHEN 7 THEN 'Refundable'
                            WHEN 8 THEN 'Resolved'
                            ELSE CAST("Status" AS TEXT)
                        END,
                        "CreatedAt", "SwapPkScript", "SwapAddress", "OfferHex", "MakerDescriptor",
                        "FromAssetId", "ToAssetId", "Invoice", "PaymentHash", "RefundLocktime",
                        "Preimage", "SpentTxid", "HtlcLocktime", "HtlcPubkey",
                        "OnchainPayoutAddress", "Metadata"
                    FROM "ArkadeSwapIntents";

                    DROP TABLE "ArkadeSwapIntents";
                    ALTER TABLE "__temp_ArkadeSwapIntents" RENAME TO "ArkadeSwapIntents";
                    CREATE INDEX "IX_ArkadeSwapIntents_PaymentHash" ON "ArkadeSwapIntents" ("PaymentHash");
                    CREATE INDEX "IX_ArkadeSwapIntents_SwapPkScript" ON "ArkadeSwapIntents" ("SwapPkScript");
                    CREATE INDEX "IX_ArkadeSwapIntents_WalletId_Status" ON "ArkadeSwapIntents" ("WalletId", "Status");
                    """);
                return;
            }

            migrationBuilder.Sql(
                """
                UPDATE "BTCPayServer.Plugins.Ark"."ArkadeSwapIntents"
                SET "Metadata" = jsonb_strip_nulls(jsonb_build_object(
                    'offerHex', "OfferHex",
                    'makerDescriptor', "MakerDescriptor",
                    'invoice', "Invoice",
                    'preimage', "Preimage",
                    'htlcPubkey', "HtlcPubkey",
                    'htlcLocktime', CASE WHEN "HtlcLocktime" IS NULL THEN NULL ELSE "HtlcLocktime"::text END,
                    'onchainPayoutAddress', "OnchainPayoutAddress"
                ))::text;

                ALTER TABLE "BTCPayServer.Plugins.Ark"."ArkadeSwapIntents"
                    ALTER COLUMN "Type" TYPE character varying(32) USING CASE "Type"
                        WHEN 0 THEN 'BtcToAsset'
                        WHEN 1 THEN 'AssetToBtc'
                        WHEN 2 THEN 'BtcToLightning'
                        WHEN 3 THEN 'LightningToBtc'
                        WHEN 4 THEN 'BtcToOnchain'
                        ELSE "Type"::text
                    END,
                    ALTER COLUMN "Status" TYPE character varying(32) USING CASE "Status"
                        WHEN 0 THEN 'Funding'
                        WHEN 1 THEN 'Pending'
                        WHEN 2 THEN 'Claimable'
                        WHEN 3 THEN 'Cancelling'
                        WHEN 4 THEN 'Fulfilled'
                        WHEN 5 THEN 'Cancelled'
                        WHEN 6 THEN 'Recoverable'
                        WHEN 7 THEN 'Refundable'
                        WHEN 8 THEN 'Resolved'
                        ELSE "Status"::text
                    END,
                    ALTER COLUMN "OfferHex" DROP NOT NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversing the string enums or deleting copied metadata would destroy post-upgrade data.
        }
    }
}
