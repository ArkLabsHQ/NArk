using NBitcoin.Secp256k1;

namespace NArk.Services.Models;

public record DeriveContractRequest(ArkOperatorTerms OperatorTerms, ECXOnlyPubKey User, byte[]? Tweak = null);