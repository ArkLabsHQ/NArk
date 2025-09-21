#!/usr/bin/env bash
set -e

log() {
  local msg="$1"
  local green="\033[0;32m"
  local reset="\033[0m"
  echo -e "${green}[$(date '+%H:%M:%S')] ${msg}${reset}"
}

INVOICE=$1
if [ -z "$INVOICE" ]; then
  echo "Usage: $0 INVOICE" >&2
  exit 1
fi

log "Paying invoice: $INVOICE"
destination_node_pubkey=$(docker exec boltz-lnd lncli --network=regtest  decodepayreq $INVOICE | jq -r ".destination")
primary_node_pubkey=$(docker exec boltz-lnd lncli --network=regtest getinfo | jq -r '.identity_pubkey')
secondary_node_pubkey=$(docker exec lnd lncli --network=regtest getinfo | jq -r '.identity_pubkey')

if [ "$destination_node_pubkey" = "$primary_node_pubkey" ]; then
  log "Paying invoice to primary lnd instance..."
  docker exec lnd lncli --network=regtest payinvoice --force $INVOICE
elif [ "$destination_node_pubkey" = "$secondary_node_pubkey" ]; then
  log "Paying invoice to secondary lnd instance..."
  docker exec boltz-lnd lncli --network=regtest payinvoice --force $INVOICE
else
  log "ERROR: Invoice destination node pubkey does not match either primary or secondary lnd instance."
  exit 1
fi