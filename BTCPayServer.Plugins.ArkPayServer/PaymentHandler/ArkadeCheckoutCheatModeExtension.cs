using BTCPayServer.Payments;
using BTCPayServer.Services;
using NBitcoin;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler
{
    public class ArkadeCheckoutCheatModeExtension(Cheater cheater) : ICheckoutCheatModeExtension
    {
        public bool Handle(PaymentMethodId paymentMethodId) => paymentMethodId == ArkadePlugin.ArkadePaymentMethodId;

        public async Task<ICheckoutCheatModeExtension.MineBlockResult> MineBlock(
            ICheckoutCheatModeExtension.MineBlockContext mineBlockContext)
        {
            // call nigiri rpc --generate  {mineBlockContext.BlockCount}
            
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "nigiri",
                    Arguments = $"rpc --generate {mineBlockContext.BlockCount}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return new ICheckoutCheatModeExtension.MineBlockResult();
            }

            throw new Exception($"Failed to generate blocks: {error}");
        }

        public async Task<ICheckoutCheatModeExtension.PayInvoiceResult> PayInvoice(ICheckoutCheatModeExtension.PayInvoiceContext payInvoiceContext)
        {
            var destination = payInvoiceContext.PaymentPrompt.Destination;
            var amt = Money.Coins(payInvoiceContext.Amount).Satoshi;

            var args = $"ark send --to {destination} --amount {amt} --password secret";
            
            // Create a process to execute the docker command
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "nigiri",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                var arkOutput = JObject.Parse(output);
                var txId = arkOutput.GetValue("txid")?.Value<string>();
                if (txId is not null)
                    return new ICheckoutCheatModeExtension.PayInvoiceResult(txId);
            }

            throw new ExternalProcessFailedException($"docker {args}", error);
        }
    }
}
