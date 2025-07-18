#!/usr/bin/env bash

set -e

log() {
  local msg="$1"
  local green="\033[0;32m"
  local reset="\033[0m"
  echo -e "${green}[$(date '+%H:%M:%S')] ${msg}${reset}"
}

setup_fulmine_wallet() {
  log "Setting up Fulmine wallet..."
  
  # Generate Seed
  log "Generating seed..."
  local seed_response=$(curl -s -X GET http://localhost:7003/api/v1/wallet/genseed)
  local private_key=$(echo "$seed_response" | jq -r '.nsec')
  log "Generated private key: $private_key"

  # Create Wallet
  log "Creating Fulmine wallet..."
  curl -X POST http://localhost:7003/api/v1/wallet/create \
       -H "Content-Type: application/json" \
       -d "{\"private_key\": \"$private_key\", \"password\": \"password\", \"server_url\": \"http://arkd:7070\"}"

  # Unlock Wallet
  log "Unlocking Fulmine wallet..."
  curl -X POST http://localhost:7003/api/v1/wallet/unlock \
       -H "Content-Type: application/json" \
       -d '{"password": "password"}'

  # Get Wallet Status
  log "Checking Fulmine wallet status..."
  local status_response=$(curl -s -X GET http://localhost:7003/api/v1/wallet/status)
  log "Wallet status: $status_response"

  # Get wallet address
  log "Getting Fulmine wallet address..."
  local address_response=$(curl -s -X GET http://localhost:7003/api/v1/address)
  local fulmine_address=$(echo "$address_response" | jq -r '.address' | sed 's/bitcoin://' | sed 's/?ark=.*//')
  log "Fulmine address: $fulmine_address"

  # Fund fulmine
  log "Funding Fulmine wallet..."
  nigiri faucet "$fulmine_address" 0.01
  
  # Wait for transaction to be processed
  sleep 5

  # Settle the transaction
  log "Settling Fulmine wallet..."
  curl -X GET http://localhost:7003/api/v1/settle

  # Get transaction history
  log "Getting transaction history..."
  curl -X GET http://localhost:7003/api/v1/transactions
  
  log "✓ Fulmine wallet setup completed successfully!"
}

# Argument parsing
CLEAN=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --clean)
      CLEAN=true
      shift
      ;;
    *)
      echo "Usage: $0 [--clean]" >&2
      exit 1
      ;;
  esac
done

# Navigate to repository root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# 1. Ensure nigiri is installed
if ! command -v nigiri >/dev/null 2>&1; then
  log "Nigiri not found. Installing..."
  curl https://getnigiri.vulpem.com | bash
else
  log "Nigiri found: $(nigiri --version)"
fi



# Create and prepare volume directories for ark
log "Setting up volume directories..."
mkdir -p "$SCRIPT_DIR/volumes/ark"
mkdir -p "$SCRIPT_DIR/volumes/ark/cors"

# Copy CORS configuration file if it exists
if [ -f "$SCRIPT_DIR/cors.nginx.conf" ]; then
  log "Copying CORS configuration..."
  cp "$SCRIPT_DIR/cors.nginx.conf" "$SCRIPT_DIR/volumes/ark/cors/cors.nginx.conf"
fi

# Clean volumes if requested
if [ "$CLEAN" = true ]; then


  # 2. Stop any running nigiri instances
  log "Stopping existing Nigiri containers..."
  nigiri stop --delete
  docker compose -f docker-compose.ark.yml down --volumes --remove-orphans
  log "Removing any previous arkd containers..."
  docker rm -f arkd ark-wallet 2>/dev/null || true
  log "Cleaning ark volume directories..."
  rm -rf "$SCRIPT_DIR/volumes/ark/*"

  #stop btcpayserver
  log "Stopping existing BTCPayServer containers..."
  docker compose -f submodules/btcpayserver/BTCPayServer.Tests/docker-compose.yml down --volumes || true
fi

  log "Starting Nigiri with Ark support..."
    nigiri start 
  
  # Use docker-compose.ark.yml for custom ark configuration
  log "Starting custom ark configuration from docker-compose.ark.yml..."
  docker compose -f docker-compose.ark.yml up -d
  
log "Nigiri containers running:"
docker ps \
  --filter "name=ark" \
  --filter "name=arkd" \
  --filter "name=arkd-wallet" \
  --filter "name=nigiri" \
  --format "table {{.Names}}\t{{.Status}}"

# 5. Start BTCPayServer dependencies
log "Starting BTCPayServer dependency containers..."
pushd submodules/btcpayserver/BTCPayServer.Tests >/dev/null
docker compose up -d dev
popd >/dev/null
log "BTCPayServer containers running:"
docker ps --filter "name=btcpay" --format "table {{.Names}}\t{{.Status}}"

# 6. Setup and unlock arkd wallet
container="arkd"

if [ -n "$container" ]; then
  log "Setting up Ark wallet..."
  
  # Create wallet with password "secret" (ignore if already exists)
  log "Creating Ark wallet..."
  docker exec "$container" arkd wallet create --password secret || true
  
  # Unlock wallet
  log "Unlocking Ark wallet..."
  docker exec "$container" arkd wallet unlock --password secret
  docker exec "$container" arkd wallet status
  log "✓ arkd wallet unlocked"
  
  # Initialize ark wallet
  log "Initializing Ark wallet..."
  docker exec "$container" ark init --network regtest --password secret --server-url localhost:7070 --explorer http://chopsticks:3000
  log "✓ ark wallet initialized"
  
  # Get wallet address and fund it
  log "Getting wallet address..."
  address=$(docker exec "$container" arkd wallet address)
  log "Funding wallet address: $address"
  
  # Fund the address using nigiri faucet (10 times for 10 BTC)
  for i in {1..10}; do
    nigiri faucet "$address"
  done
  sleep 5
  # print arkd balance
  log "Ark wallet balance: $(docker exec "$container" arkd wallet balance)"
  
  # Get boarding address
  log "Getting boarding address..."
  boarding_response=$(docker exec "$container" ark receive)
  boarding_address=$(echo "$boarding_response" | jq -r '.boarding_address')
  
  if [ -n "$boarding_address" ]; then
    log "Funding boarding address: $boarding_address"
    nigiri faucet "$boarding_address"
    
    # Wait a bit for the transaction to be processed
     sleep 5
    
     # Settle the wallet in the background
     log "Settling wallet in background..."
     docker exec "$container" ark settle --password secret &
     log "✓ Ark setup initiated successfully!"
  else
    log "Failed to get boarding address"
  fi
  
  # 7. Setup Fulmine wallet
  setup_fulmine_wallet

  # # Setup LND for Lightning swaps
  # log "Setting up LND for Lightning swaps..."
  # sleep 10  # Give LND time to start

  # # Create wallet in LND if needed
  # log "Creating LND wallet..."
  # docker exec boltz-lnd lncli --network=regtest create 2>/dev/null || true

  # # Unlock wallet if needed
  # log "Unlocking LND wallet..."
  # docker exec boltz-lnd lncli --network=regtest unlock --password="" 2>/dev/null || true

  # # Fund LND wallet
  # log "Getting LND address..."
  # ln_address=$(docker exec boltz-lnd lncli --network=regtest newaddress p2wkh | jq -r '.address')
  # log "LND address: $ln_address"
  # log "Funding LND wallet..."
  # nigiri faucet "$ln_address" 1

  # # Wait for confirmation
  # log "Waiting for LND funding confirmation..."
  # sleep 10

  # # Check LND balance
  # log "LND balance:"
  # docker exec boltz-lnd lncli --network=regtest walletbalance
else
  log "Ark container not running; wallet not setup"
fi

log "✅ Development environment ready."
log "\nServices available at:\n"
log "Ark wallet: http://localhost:6060"
log "Ark daemon: http://localhost:7070"
log "Boltz API: http://localhost:9001"
log "Boltz WebSocket: ws://localhost:9004"
log "CORS proxy: http://localhost:9069"
log "Fulmine: http://localhost:7002"
log "LND gRPC: localhost:10010"
log "Chopsticks (Bitcoin explorer): http://localhost:3000"