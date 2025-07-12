#!/usr/bin/env bash

set -e

log() {
  local msg="$1"
  local green="\033[0;32m"
  local reset="\033[0m"
  echo -e "${green}[$(date '+%H:%M:%S')] ${msg}${reset}"
}

# Argument parsing
ARKD_BRANCH=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --arkd-branch)
      ARKD_BRANCH="$2"
      shift 2
      ;;
    *)
      echo "Usage: $0 [--arkd-branch <branch>]" >&2
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

# 2. Stop any running nigiri instances
log "Stopping existing Nigiri containers..."
nigiri stop --delete
log "Removing any previous arkd containers..."
docker rm -f arkd arkd-wallet 2>/dev/null || true

# 3. Start nigiri and arkd wallet
if [ -z "$ARKD_BRANCH" ]; then
  log "Starting Nigiri with Ark support..."
  nigiri start --ark
else
  log "Starting Nigiri without Ark support..."
  nigiri start
  TS_SDK_DIR="$SCRIPT_DIR/.cache/ts-sdk-$ARKD_BRANCH"
  rm -rf "$TS_SDK_DIR"
  log "Cloning ts-sdk ($ARKD_BRANCH)..."
  git clone --depth 1 --branch "$ARKD_BRANCH" https://github.com/arkade-os/ts-sdk.git "$TS_SDK_DIR"
  pushd "$TS_SDK_DIR" >/dev/null
  docker compose -f docker-compose.yml build --no-cache
  docker compose up -d
  popd >/dev/null
  rm -rf "$TS_SDK_DIR"
fi
log "Nigiri containers running:"
docker ps \
  --filter "name=ark" \
  --filter "name=arkd" \
  --filter "name=arkd-wallet" \
  --filter "name=nigiri" \
  --format "table {{.Names}}\t{{.Status}}"

# 4. Remove previous BTCPayServer dependency containers
log "Cleaning old BTCPayServer dependencies..."
pushd submodules/btcpayserver/BTCPayServer.Tests >/dev/null
if [ -f docker-compose.yml ]; then
  docker compose down --volumes || true
fi
popd >/dev/null

# 5. Start BTCPayServer dependencies
log "Starting BTCPayServer dependency containers..."
pushd submodules/btcpayserver/BTCPayServer.Tests >/dev/null
docker compose up -d dev
popd >/dev/null
log "BTCPayServer containers running:"
docker ps --filter "name=btcpay" --format "table {{.Names}}\t{{.Status}}"

# 6. Unlock arkd wallet
log "Unlocking arkd wallet..."
container=""
if docker ps --format '{{.Names}}' | grep -q '^ark$'; then
  container="ark"
elif docker ps --format '{{.Names}}' | grep -q '^arkd$'; then
  container="arkd"
fi

if [ -n "$container" ]; then
  docker exec "$container" arkd wallet create --password secret || true
  docker exec "$container" arkd wallet unlock --password secret
  docker exec "$container" arkd wallet status
  log "arkd wallet unlocked"
  
  docker exec "$container" ark init --network regtest --password secret --server-url localhost:7070 --explorer http://chopsticks:3000
  log "ark wallet initialized"
  
  # use nigiri faucet to `arkd wallet address`
  address=$(docker exec -ti "$container" arkd wallet address)
  nigiri faucet $address
  address=$(docker exec -ti "$container" ark receive)
  # parse json and take boarding_address
  address=$(echo "$address" | jq -r '.boarding_address')
  nigiri faucet "$address"
  
  docker exec "$container" ark settle --password secret
  
  
else
  log "Ark container not running; wallet not unlocked"
fi

log "✅ Development environment ready."