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

## Build
Run `dotnet build NArk.sln` to compile all projects (no tests yet).

## Agent Guidance
- Follow standard .NET naming conventions.
- Ensure the solution builds successfully before committing.
- Keep this file up to date when repo structure changes.
