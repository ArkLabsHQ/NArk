#!/usr/bin/env bash

if [ -z "${CI:-}" ]; then
  # Initialize the server submodule
  git submodule init && git submodule update --recursive

  # Install the workloads
  dotnet workload restore
fi

# Create appsettings file to include app plugin when running the server
appsettings="submodules/btcpayserver/BTCPayServer/appsettings.dev.json"
if [ ! -f $appsettings ]; then
    echo '{ "DEBUG_PLUGINS": "../../../BTCPayServer.Plugins.ArkPayServer/bin/Debug/net8.0/BTCPayServer.Plugins.ArkPayServer.dll" }' > $appsettings
fi

# Publish plugin to share its dependencies with the server
cd BTCPayServer.Plugins.ArkPayServer
dotnet publish -c Debug -o bin/Debug/net8.0
cd -
