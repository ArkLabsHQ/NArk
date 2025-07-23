using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Services.Models;

public record ArkOperatorTerms(Money Dust, ECXOnlyPubKey SignerKey, Network Network, Sequence UnilateralExit);