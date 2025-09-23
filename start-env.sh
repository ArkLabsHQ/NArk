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
  
  # Wait for Fulmine service to be ready
  log "Waiting for Fulmine service to be ready..."
  max_attempts=15
  attempt=1
  while [ $attempt -le $max_attempts ]; do
    if curl -s http://localhost:7003/api/v1/wallet/status >/dev/null 2>&1; then
      log "Fulmine service is ready!"
      break
    fi
    log "Waiting for Fulmine service... (attempt $attempt/$max_attempts)"
    sleep 2
    ((attempt++))
  done

  if [ $attempt -gt $max_attempts ]; then
    log "ERROR: Fulmine service failed to start within expected time"
    exit 1
  fi

  # Generate Seed first
  log "Generating seed..."
  seed_response=$(curl -s -X GET http://localhost:7003/api/v1/wallet/genseed)
  private_key=$(echo "$seed_response" | jq -r '.nsec')
  log "Generated private key: $private_key"
  
  # Create Wallet with the generated private key (with retry)
  log "Creating Fulmine wallet..."
  curl -X POST http://localhost:7003/api/v1/wallet/create \
       -H "Content-Type: application/json" \
       -d "{\"private_key\": \"$private_key\", \"password\": \"password\", \"server_url\": \"http://ark:7070\"}"
  
  # Unlock Wallet
  log "Unlocking Fulmine wallet..."
  curl -X POST http://localhost:7003/api/v1/wallet/unlock \
       -H "Content-Type: application/json" \
       -d '{"password": "password"}'
       
  # Get Wallet Status
  log "Checking Fulmine wallet status..."
  local status_response=$(curl -s -X GET http://localhost:7003/api/v1/wallet/status)
  log "Wallet status: $status_response"

  # Get wallet address (with retry)
  log "Getting Fulmine wallet address..."
  max_attempts=5
  attempt=1
  local fulmine_address=""
  while [ $attempt -le $max_attempts ]; do
    local address_response=$(curl -s -X GET http://localhost:7003/api/v1/address)
    fulmine_address=$(echo "$address_response" | jq -r '.address' | sed 's/bitcoin://' | sed 's/?ark=.*//')
    
    if [[ "$fulmine_address" != "null" && -n "$fulmine_address" ]]; then
      log "Fulmine address: $fulmine_address"
      break
    fi
    
    log "Address not ready yet (attempt $attempt/$max_attempts), waiting..."
    sleep 2
    ((attempt++))
  done

  if [[ "$fulmine_address" == "null" || -z "$fulmine_address" ]]; then
    log "ERROR: Failed to get valid Fulmine wallet address"
    exit 1
  fi

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
elif [ "$CLEAN" = true ]; then
  log "Nigiri found but clean flag set. Reinstalling..."
else
  log "Nigiri found: $(nigiri --version)"
fi

# Clean volumes if requested
if [ "$CLEAN" = true ]; then

  # 2. Stop any running nigiri instances
  log "Stopping existing Nigiri containers..."
  docker compose -f docker-compose.ark.yml down --volumes --remove-orphans
  nigiri stop --delete

  #stop btcpayserver
  log "Stopping existing BTCPayServer containers..."
  docker compose -f submodules/btcpayserver/BTCPayServer.Tests/docker-compose.yml down --volumes || true
fi
log "Starting Nigiri with Ark support..."
# Start nigiri, but don't fail if it's already running
nigiri start --ark || {
  if [[ $? -eq 1 ]]; then
    log "Nigiri may already be running, continuing..."
  else
    log "Failed to start nigiri with unexpected error"
    exit 1
  fi
}
# Use docker-compose.ark.yml for custom ark configuration
log "Startinga stack with docker-compose.ark.yml..."
docker compose -f docker-compose.ark.yml up -d
  

# 5. Start BTCPayServer dependencies
#log "Starting BTCPayServer dependency containers..."
#pushd submodules/btcpayserver/BTCPayServer.Tests >/dev/null
#docker compose up -d dev
#popd >/dev/null
#log "BTCPayServer containers running:"
#docker ps --filter "name=btcpay" --format "table {{.Names}}\t{{.Status}}"

# 6. Setup and unlock arkd wallet
container="ark"

# Wait for arkd to be ready
log "Waiting for arkd to be ready..."
max_attempts=30
attempt=1
while [ $attempt -le $max_attempts ]; do
  if curl -s http://localhost:7070/health >/dev/null 2>&1; then
    log "arkd is ready!"
    break
  fi
  log "Waiting for arkd... (attempt $attempt/$max_attempts)"
  sleep 2
  ((attempt++))
done

if [ $attempt -gt $max_attempts ]; then
  log "ERROR: arkd failed to start within expected time"
  exit 1
fi

nigiri ark init --network regtest --password secret --server-url localhost:7070 --explorer http://chopsticks:3000
nigiri faucet $(nigiri ark receive | jq -r ".onchain_address") 2
nigiri ark redeem-notes -n $(nigiri arkd note --amount 100000000) --password secret

# 7. Setup Fulmine wallet
setup_fulmine_wallet

#
#  docker exec -i boltz-lnd bash -c \
#    'echo -n "lndconnect://boltz-lnd:10009?cert=$(grep -v CERTIFICATE /root/.lnd/tls.cert \
#       | tr -d = | tr "/+" "_-")&macaroon=$(base64 /root/.lnd/data/chain/bitcoin/regtest/admin.macaroon \
#       | tr -d = | tr "/+" "_-")"' | tr -d '\n'


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