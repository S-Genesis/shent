#!/usr/bin/env bash
# Inicia un nodo SHENT en Linux o macOS.
#
#   ./scripts/start-node.sh                                         primer nodo (espera conexiones)
#   BOOTSTRAP_NODES=/ip4/IP_PUBLICA/tcp/37373/p2p/PEER_ID ./scripts/start-node.sh
#                                                                   segundo nodo (reemplazá IP_PUBLICA y PEER_ID)
#   ./scripts/start-node.sh --relay --announce /ip4/IP_DEL_SERVIDOR/tcp/37373
#                                                                   nodo de arranque + relay en un servidor
#   ./scripts/start-node.sh id                                      muestra el Peer ID sin iniciar el nodo
#
# Variables: SHENT_NETWORK (mainnet por defecto), P2P_PORT, BOOTSTRAP_NODES, SHENT_DATADIR, SHENT_RELAY, SHENT_UPNP.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export SHENT_NETWORK="${SHENT_NETWORK:-mainnet}"

PUBLISHED="$ROOT/publish/shent-node/shent-node"
if [[ -x "$PUBLISHED" ]]; then
  exec "$PUBLISHED" "$@"
fi

DOTNET="$(command -v dotnet || echo "$HOME/.dotnet/dotnet")"
exec "$DOTNET" run --project "$ROOT/src/Shent.Node" -c Release -- "$@"
