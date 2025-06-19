# NArk Repo Guide
Integrates Ark payment functionality with BTCPayServer. Everything targets .NET 8.

## Structure
- `NArk`: constructs Ark taproot contracts and scripts.
- `NArk.Grpc`: gRPC client library generated from `Protos/ark/v1`.
- `BTCPayServer.Plugins.ArkPayServer`: plugin using EF Core with PostgreSQL to store Ark wallet data and call Ark gRPC services.
- `NArk/Boltz`: REST client for the Boltz swap API.
- `submodules/btcpayserver`: BTCPayServer source pulled as a submodule.

## Setup
Run `./setup.sh` (or `./setup.ps1` on Windows) after cloning to pull submodules, restore workloads, create a plugin entry in the server config and publish the plugin. Use `./add-migration.sh <Name>` to add EF Core migrations.

## Agent Guidance
- Follow standard .NET naming conventions.
- Ensure the solution builds successfully before committing.
- Keep this file up to date when repo structure changes.

## Conceptual Overview
This repository will contain a plugin for BTCPayServer that enables merchants to accept Bitcoin payments through Ark—a self-custodial, offchain protocol built directly on Bitcoin. Ark uses Virtual UTXOs (VTXOs) to facilitate instant, low-cost transactions that can later be anchored onchain for finality.

## The Goal
The plugin is intended to support three key flows:

- **Ark-native**: enabling direct offchain VTXO-to-VTXO payments within the Ark network.
- **Boltz-Ark**: enabling trustless Lightning-to-Ark swaps using BOLT11 invoices via Boltz.
- **Boarding Address Flow**: allowing users to enter the Ark system by funding a Taproot “boarding address,” which is converted into a VTXO with help from the Ark Operator. If the Operator is unresponsive, users should be able to reclaim funds unilaterally after a timelock.

Each of these flows is implemented as a type of contract, created using a wallet defined by a Miniscript descriptor and an address derivation index.

## Ark Concepts

- **VTXOs**: Offchain Bitcoin outputs secured via collaborative (user + operator) and unilateral (timelocked) Taproot paths. These are the basic payment units in Ark.
- **Contracts**: Payment flows (Ark-native and Boltz) are modeled as Taproot contracts generated from a descriptor and derivation index. Each contract results in a unique payment address.
- **Boarding Addresses**: Onchain Taproot addresses that act as trust-minimized entry points to Ark. When funded, they allow the plugin to request the Ark Operator to convert the UTXO into a VTXO.
- **Commitment Transactions**: Onchain transactions created by the operator that anchor offchain VTXO state into Bitcoin, securing it with Bitcoin-level finality.

## Arkade OS Integration
Arkade extends Ark with programmability and verifiability. It introduces:

- **Arkade Script**: A scripting layer that expands Bitcoin Script for more expressive offchain contracts.
- **Arkade Signer**: A TEE-secured module that co-signs VTXO transactions and prevents double-spends through hardware-enforced integrity.

The plugin will integrate with Arkade to:

- Generate boarding addresses and contracts
- Detect onchain deposits and initiate VTXO creation
- Subscribe to contract activity from the operator
- Anchor contracts onchain when requested or required

## Design Considerations

- Wallets will use Miniscript descriptors and must manage address derivation state safely.
- All contracts must enforce a secure unilateral exit path for the user.
- Boarding addresses must be monitored and resolved into VTXOs.
- Payment detection will be event-driven, based on Arkade Operator subscriptions.
- Contracts may remain offchain until anchored, enabling both instant and deferred finality models.

## Problem Domain Summary
This plugin aims to bridge Bitcoin-native offchain protocols with BTCPayServer’s payment system. It will eventually manage:

- Onboarding via boarding addresses
- Contract generation and tracking
- Offchain event monitoring through Arkade
- Invoice status updates based on offchain state transitions

This is an early-stage implementation. Most components are not yet built, but this file defines the intended scope, conceptual architecture, and integration points that the system will target as development progresses.
