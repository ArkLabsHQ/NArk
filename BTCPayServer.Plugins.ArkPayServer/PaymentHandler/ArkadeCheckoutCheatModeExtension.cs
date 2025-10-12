using System.Runtime.InteropServices;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using NBitcoin;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler
{
    public class ArkadeCheckoutCheatModeExtension(Cheater cheater) : ICheckoutCheatModeExtension
    {
        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        
        public bool Handle(PaymentMethodId paymentMethodId) => paymentMethodId == ArkadePlugin.ArkadePaymentMethodId;

        public async Task<ICheckoutCheatModeExtension.MineBlockResult> MineBlock(
            ICheckoutCheatModeExtension.MineBlockContext mineBlockContext)
        {
            var (fileName, arguments) = GetProcessInfo($"rpc --generate {mineBlockContext.BlockCount}");
            
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
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

            var nigiriArgs = $"ark send --to {destination} --amount {amt} --password secret";
            var (fileName, arguments) = GetProcessInfo(nigiriArgs);
            
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
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

            throw new ExternalProcessFailedException($"nigiri {nigiriArgs}", error);
        }

        /// <summary>
        /// Returns the appropriate process info for executing nigiri commands.
        /// On Windows, uses WSL to execute nigiri. On Linux/macOS, executes directly.
        /// </summary>
        private static (string FileName, string Arguments) GetProcessInfo(string nigiriArgs)
        {
            if (IsWindows)
            {
                // On Windows, use WSL to execute nigiri
                return ("wsl", $"nigiri {nigiriArgs}");
            }
            
            // On Linux/macOS, execute nigiri directly
            return ("nigiri", nigiriArgs);
        }
    }
}
