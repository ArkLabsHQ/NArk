using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using NBitcoin;
using System;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler
{
    public class ArkadeCheckoutCheatModeExtension : ICheckoutCheatModeExtension
    {
        private readonly Cheater _cheater;
        public ArkadeCheckoutCheatModeExtension(Cheater cheater)
        {
            _cheater = cheater;
        }
        public BTCPayNetwork Network { get; } 

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
            //call:  docker exec -ti arkd ark send --to {destination} --amount {amt}

                // Create process to execute the docker command
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "docker",
                        Arguments = $"exec -i ark ark send --to {destination} --amount {amt}",
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
                    JObject jObject = JObject.Parse(output);

                    return new ICheckoutCheatModeExtension.PayInvoiceResult(jObject["txid"].Value<string>());

                }
                
throw new Exception(error);

            
        }
    }
}
