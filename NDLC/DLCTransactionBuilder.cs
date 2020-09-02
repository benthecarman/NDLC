﻿using System;
using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using NDLC.Secp256k1;

namespace NDLC.Messages
{
	public class DLCTransactionBuilder
	{
		private readonly Offer? offer;
		private readonly Accept? accept;
		private readonly Sign? sign;
		private readonly Network network;

		class Party
		{
			public Party(PubKey pubKey,CetSigs cetSigs)
			{
				this.CetSigs = cetSigs;
				this.PubKey = pubKey;
			}
			public CetSigs CetSigs { get; }
			public PubKey PubKey { get; }
		}

		Party? initiator;
		Party? acceptor;
		Party? remote;
		Party? me;

		public Transaction? FundingOverride { get; set; }

		public DLCTransactionBuilder(bool isInitiator, Offer? offer, Accept? accept, Sign? sign, Network network)
		{
			this.offer = offer;
			this.accept = accept;
			this.sign = sign;
			this.network = network;

			{
				if (offer?.PubKeys?.FundingKey is PubKey p && sign?.CetSigs is CetSigs s)
				{
					initiator = new Party(p, s);
				}
			}
			{
				if (accept?.PubKeys?.FundingKey is PubKey p && accept?.CetSigs is CetSigs s)
				{
					acceptor = new Party(p, s);
				}
			}
			if (isInitiator)
			{
				me = initiator;
				remote = acceptor;
			}
			else
			{
				me = acceptor;
				remote = initiator;
			}
		}

		public Transaction BuildRefund()
		{
			if (offer?.Timeouts is null || offer?.TotalCollateral is null || accept?.TotalCollateral is null)
				throw new InvalidOperationException("We did not received enough data to create the refund");
			var funding = BuildFunding();
			Transaction tx = network.CreateTransaction();
			tx.Version = 2;
			tx.LockTime = offer.Timeouts.ContractTimeout;
			tx.Inputs.Add(new OutPoint(funding.GetHash(), 0), sequence: 0xFFFFFFFE);
			tx.Outputs.Add(offer.TotalCollateral, offer.PubKeys!.PayoutAddress);
			tx.Outputs.Add(accept.TotalCollateral, accept.PubKeys!.PayoutAddress);
			return tx;
		}

		public Transaction BuildFunding()
		{
			if (FundingOverride is Transaction)
				return FundingOverride;
			if (offer?.FundingInputs is null || accept?.FundingInputs is null)
				throw new InvalidOperationException("We did not received enough data to create the funding");

			Transaction tx = network.CreateTransaction();
			tx.Version = 2;
			tx.LockTime = 0;
			var fundingScript = GetFundingScript();
			var p2wsh = fundingScript.WitHash.ScriptPubKey;
			foreach (var input in offer.FundingInputs!)
			{
				tx.Inputs.Add(input.Outpoint, Script.Empty);
			}
			foreach (var input in accept.FundingInputs!)
			{
				tx.Inputs.Add(input.Outpoint, Script.Empty);
			}
			foreach (var input in tx.Inputs)
				input.Sequence = 0xffffffff;
			var total_change_length = (offer.ChangeAddress?.ScriptPubKey?.Length ?? 0)
									+ (accept.ChangeAddress?.ScriptPubKey?.Length ?? 0);
			var weight = 286 + 4 * total_change_length + 272 * tx.Inputs.Count;

			var total_output_length = total_change_length + 8;
			var max_future_weight = 500 + 4 * total_output_length;

			tx.Outputs.Add(offer.TotalCollateral! + accept.TotalCollateral!
						   + offer.FeeRate!.GetFee(max_future_weight / 4), p2wsh);
			var vBytePerUser = Math.DivRem(max_future_weight + weight, 8, out var r);

			var totalInput = offer.FundingInputs.Select(s => s.Output.Value).Sum();
			tx.Outputs.Add(totalInput
						- offer.TotalCollateral
						- offer.FeeRate!.GetFee(vBytePerUser), offer.ChangeAddress);

			totalInput = accept.FundingInputs.Select(s => s.Output.Value).Sum();
			tx.Outputs.Add(totalInput
						- accept.TotalCollateral
						- offer.FeeRate!.GetFee(vBytePerUser)
						//- offer.FeeRate!.GetFee((r + 7) / 8)
						, accept.ChangeAddress);
			return tx;
		}

		private Script GetFundingScript()
		{
			if (offer?.PubKeys?.FundingKey is null || accept?.PubKeys?.FundingKey is null)
				throw new InvalidOperationException("We did not received enough data to create the funding script");
			return PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, offer.PubKeys!.FundingKey, accept.PubKeys!.FundingKey);
		}

		public Transaction BuildCET(uint256 outcome)
		{
			if (offer?.Timeouts is null || offer?.TotalCollateral is null || accept?.TotalCollateral is null)
				throw new InvalidOperationException("We did not received enough data to create the refund");
			var initiatorPayout = offer.ContractInfo.Where(c => c.SHA256 == outcome).Select(c => c.Sats).SingleOrDefault();
			if (initiatorPayout is null)
				throw new InvalidOperationException("Invalid outcome");

			var funding = BuildFunding();
			Transaction tx = network.CreateTransaction();
			tx.Version = 2;
			tx.LockTime = offer.Timeouts.ContractMaturity;
			tx.Inputs.Add(new OutPoint(funding.GetHash(), 0), sequence: 0xFFFFFFFE);

			var collateral = offer.TotalCollateral + accept.TotalCollateral;
			tx.Outputs.Add(initiatorPayout, offer.PubKeys!.PayoutAddress);
			tx.Outputs.Add(collateral - initiatorPayout, accept.PubKeys!.PayoutAddress);
			foreach (var output in tx.Outputs.ToArray())
			{
				if (output.Value < Money.Satoshis(1000))
					tx.Outputs.Remove(output);
			}
			return tx;
		}

		public bool VerifyRemoteCetSigs()
		{
			if (remote is null || offer?.ContractInfo is null)
				throw new InvalidOperationException("We did not received enough data to verify the sigs");

			foreach (var outcome in offer.ContractInfo.Select(i => i.SHA256))
			{
				var outcomeSig = remote.CetSigs.OutcomeSigs![outcome];
				if (!offer.ContractInfo.Any(ci => ci.SHA256 == outcome))
					return false;

				if (!offer.OracleInfo!.TryComputeSigpoint(outcome, out var sigpoint) || sigpoint is null)
					return false;
				var ecPubKey = remote.PubKey.ToECPubKey();

				var fundingCoin = GetFundingCoin();
				var msg = BuildCET(outcome).GetSignatureHash(fundingCoin).ToBytes();
				if (!ecPubKey.SigVerify(outcomeSig.Signature, outcomeSig.Proof, msg, sigpoint))
					return false;
			}
			return true;
		}

		private ScriptCoin GetFundingCoin()
		{
			return BuildFunding().Outputs.AsCoins().First().ToScriptCoin(GetFundingScript());
		}

		public bool VerifyRemoteRefundSignature()
		{
			if (remote is null)
				throw new InvalidOperationException("We did not received enough data to verify refund signature");
			var refund = BuildRefund();
			if (remote.CetSigs.RefundSig!.PubKey != remote.PubKey)
				return false;
			if (remote.CetSigs.RefundSig.Signature.SigHash != SigHash.All)
				return false;
			if (!remote.PubKey.Verify(refund.GetSignatureHash(GetFundingCoin()), remote.CetSigs.RefundSig.Signature.Signature))
				return false;
			return true;
		}
	}
}