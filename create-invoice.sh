#!/usr/bin/env bash
set -e

log() {
  local msg="$1"
  local green="\033[0;32m"
  local reset="\033[0m"
  echo -e "${green}[$(date '+%H:%M:%S')] ${msg}${reset}"
}

# Argument parsing
SECONDARY=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --secondary)
      SECONDARY=true
      shift
      ;;
    *)
      echo "Usage: $0 [--secondary]" >&2
      exit 1
      ;;
  esac
done


if [ "$SECONDARY" = true ]; then
  log "Creating invoice on secondary lnd instance..."
  LND_CONTAINER="lnd"
else
  log "Creating invoice on primary lnd instance..."
  LND_CONTAINER="boltz-lnd"
fi

# Create invoice
INVOICE_AMOUNT=100000  # Amount in satoshis
log "Creating invoice for $INVOICE_AMOUNT sats..."
invoice=$(docker exec $LND_CONTAINER lncli --network=regtest addinvoice --amt $INVOICE_AMOUNT | jq -r '.payment_request')
log "Invoice created: $invoice"

exit 0