# SHENT

A peer-to-peer community electronic cash system. Proof of work, no central server, no company, no price.

**Guide / Guía:** [Español](docs/guide/GUIDE.es.md) · [English](docs/guide/GUIDE.en.md) · [Português](docs/guide/GUIDE.pt.md) · [Français](docs/guide/GUIDE.fr.md) · [Deutsch](docs/guide/GUIDE.de.md) · [Italiano](docs/guide/GUIDE.it.md) · [Русский](docs/guide/GUIDE.ru.md) · [中文](docs/guide/GUIDE.zh.md) · [日本語](docs/guide/GUIDE.ja.md) · [Türkçe](docs/guide/GUIDE.tr.md)

---

SHENT is a full node, wallet and CPU miner in a single desktop application. Nodes find each other on
their own (public BitTorrent DHT, local network, peer exchange), validate every block and transaction
locally, and follow the valid chain with the most accumulated work.

| | |
|---|---|
| Block time | ~10 minutes, difficulty retargeted every block over the last 60 |
| Reward | 50 SHENT per block, halving every 1,000,000 blocks |
| Supply | never more than 100,000,000 SHENT (1 SHENT = 100,000,000 units, integer arithmetic) |
| Proof of work | Argon2id, 2 MiB per attempt (memory-hard, CPU friendly) |
| Signatures | Ed25519; addresses are Bech32m (`shent1…`) |
| Model | accounts with sequential nonces; signatures bound to the network |
| Wallet | keys generated locally, stored encrypted (Argon2id + ChaCha20-Poly1305) |
| Privacy | fresh receive addresses, Dandelion++, optional Tor (SOCKS5) |
| Interface | 10 languages |

The genesis block reads: `SHENT 13/10/2026 - moneda comunitaria sin valor monetario`
("community coin with no monetary value"). SHENT is not traded anywhere and the software does not buy or
sell anything.

## Download

Windows 10/11 (64-bit): get `SHENT-<version>-win-x64.zip` from the
[Releases](../../releases) page, check its SHA-256, unzip it and run `SHENT.exe`. Nothing else needs to be
installed.

## Build from source

Requires the .NET 10 SDK.

```
dotnet build Shent.sln
dotnet test tests/Shent.Core.Tests
dotnet run --project src/Shent.Desktop        # desktop app
dotnet run --project src/Shent.Node -- help   # headless node
```

`scripts/publish-windows.ps1` produces the self-contained Windows release.

## Layout

| Path | Contents |
|---|---|
| `src/Shent.Core` | consensus, chain (SQLite), mempool, wallet, P2P network, local read-only API |
| `src/Shent.Desktop` | Avalonia desktop application |
| `src/Shent.Node` | headless node (`shent-node`) |
| `tests/Shent.Core.Tests` | unit and network tests |
| `docs/` | guide and network documentation ([P2P_INTERNET.md](docs/P2P_INTERNET.md), in Spanish) |

## Security

- The blockchain and the P2P protocol are **not encrypted**: blocks, transactions and balances are public
  and travel in the clear, signed and sealed by proof of work. Private keys are encrypted and never leave
  the user's computer.
- There is **no recovery mechanism**: losing both the `.shent` backup and its password means losing the
  coins.
- An attacker with more hash power than the rest of the network can rewrite recent blocks. A young network
  with few miners is weak. No system is invulnerable; this software is provided as is.

Never commit wallets (`*.shent`), `node_key.json`, `.env` files or real configuration. Configuration
examples: [`.env.example`](.env.example), [`shent.conf.example`](shent.conf.example).

## License

[MIT](LICENSE)
