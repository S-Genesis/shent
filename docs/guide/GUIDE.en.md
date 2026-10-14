# SHENT: A Peer-to-Peer Community Electronic Cash System

**Languages:** [Español](GUIDE.es.md) · [English](GUIDE.en.md) · [Português](GUIDE.pt.md) · [Français](GUIDE.fr.md) · [Deutsch](GUIDE.de.md) · [Italiano](GUIDE.it.md) · [Русский](GUIDE.ru.md) · [中文](GUIDE.zh.md) · [日本語](GUIDE.ja.md) · [Türkçe](GUIDE.tr.md)

---

## Abstract

A purely peer-to-peer version of electronic cash would allow payments to be sent from one person to another
without going through any institution. Digital signatures solve part of the problem, but the main benefits
are lost if a trusted third party is still required to prevent the same coin from being spent twice. SHENT
solves double-spending with a peer-to-peer network. The network records transactions in a blockchain, and
each block is sealed by a proof-of-work. The resulting record cannot be changed without redoing that work.
The longest chain, measured in accumulated work and not in number of blocks, serves as proof of the sequence
of events and as proof that it came from the largest pool of computing power. As long as most of that power
is held by nodes that are not cooperating to attack the network, those nodes generate the longest chain and
outpace any attacker. The network itself requires minimal structure. Messages are broadcast on a best-effort
basis, and nodes can leave and rejoin at will, accepting the chain with the most work as proof of what
happened while they were gone.

SHENT is a community currency. The genesis block says so in its own text:
`SHENT 13/10/2026 - moneda comunitaria sin valor monetario` (community currency with no monetary value). It
is not listed on any market, the application does not buy or sell anything, and no one promises that it will
be worth anything.

---

## 1. Introduction

The electronic money we use every day almost always depends on intermediaries that process payments. The
system works, but it has the weaknesses inherent in a trust-based model: transactions can be reversed,
mediation costs limit small payments, and each party has to hand over more information than necessary.

What is needed is a payment system based on cryptographic proof instead of trust, allowing any two people to
transact directly with each other. Transactions that are computationally impossible to reverse protect the
recipient. The network, which records the order of transactions and seals it with work, protects against
double-spending.

SHENT is an implementation of that idea: a full node with wallet, mining and P2P network, which runs on an
ordinary personal computer.

## 2. Accounts and transactions

SHENT uses an account model. Each account is identified by an **address**, derived from an Ed25519 public
key: the first 20 bytes of its SHA-256, encoded in Bech32m with the network prefix (`shent1…` on the main
network, `tshent1…` on the test network, `rshent1…` on the local one). The address does not reveal the
public key until the account sends its first transaction.

Each account has a **balance** and a **nonce**, a counter of sent transactions. A transfer contains:

- the sender's public key,
- the destination address,
- the amount and the fee,
- the account's next nonce,
- an optional expiration height,
- an optional memo of up to 80 bytes,
- and an Ed25519 signature over all of the above and over the network identifier.

The nonce serves the purpose that, in other designs, is served by references to previous outputs: each
transaction of an account can be used only once and in order. Two transactions with the same nonce are an
attempt at double-spending, and the network accepts at most one. Since the signature includes the network
identifier, a transaction that is valid on the test network is of no use on the main network.

Amounts are stored as integers: 1 SHENT = 100,000,000 units. Floating-point numbers are not used for money.

## 3. Blocks and chain

Each block contains a header and a list of transactions. The header includes the hash of the previous block,
the Merkle root of the transactions, the timestamp, the difficulty target and the proof-of-work nonce. Since
each block includes the hash of the previous one, they form a chain. To change a block, the work of that
block and of all the blocks that came after it would have to be redone.

A block is valid only if these conditions are met:

- The timestamp is greater than the median of the previous 11 blocks and is no more than 5 minutes in the
  future.
- The block is at most 1,000,000 bytes in size.
- The first transaction is the miner's reward (coinbase) and pays exactly what is due.
- All other transactions have valid signatures, nonces in order and sufficient balance.

## 4. Proof-of-work

To seal a block, a nonce must be found such that the hash of the header falls below the difficulty target.
SHENT uses **Argon2id** as the hash function for the proof-of-work, with 2 MiB of memory per attempt. It is a
"memory-hard" function: each attempt requires memory as well as computation, which brings the performance of
a personal CPU closer to that of specialized hardware. It does not make such hardware impossible: it only
reduces its advantage.

There is no shortcut to finding that nonce: each attempt is independent and the probability of success is
the same for each one. Verifying it, by contrast, costs a single hash.

The difficulty is recalculated at every block by looking at the last 60, with the goal of producing **one
block every 10 minutes on average**, whether there are few miners or many. If power is added, the difficulty
rises; if it is withdrawn, it falls.

## 5. Network

The steps the network follows are:

1. New transactions are broadcast to all nodes.
2. Each node collects new transactions into a block.
3. Each mining node works on finding the proof-of-work for its block.
4. When a node finds it, it broadcasts the block to all other nodes.
5. The other nodes accept the block only if all transactions in it are valid and not already spent.
6. Nodes express their acceptance by working on the next block, using the hash of the accepted block as the
   previous block.

A node does not trust any other node. Every block and every transaction is verified locally against the
consensus rules, even when it comes from a known node. A network message cannot credit coins by itself: only
a valid block within the chain with the most work changes balances.

To find each other, nodes do not depend on a central server. They use three channels at the same time:

- the public BitTorrent DHT, where each node publishes its port under a key derived from the network and its
  genesis block;
- discovery on the local network;
- exchange of addresses between peers.

Each address found goes through a handshake in which both sides sign a random challenge with their Ed25519
identity. For the network to work, it is enough for at least one node to accept incoming connections (TCP
port 37373 open on the main network). Nodes without open ports can receive connections through a relay.

## 6. Incentive

By convention, the first transaction in each block creates new coins owned by whoever mined the block. This
is the incentive to support the network and the only way coins enter circulation, with no authority to issue
them.

- The reward is **50 SHENT per block**.
- It is halved every **1,000,000 blocks** (about 19 years with 10-minute blocks).
- The total never exceeds **100,000,000 SHENT**.
- In addition, the miner collects the fees of the transactions it includes.

The reward goes to whoever finds the block and is not shared. With many miners, each one finds blocks with a
frequency proportional to its share of the total computing power. On average the result is the same as a
distribution, but in the form of whole, random prizes.

The incentive also helps keep nodes honest. Anyone who assembled more computing power than all the others
combined would have to choose between using it to rewrite history or using it to earn new coins by following
the rules. They are better off playing by the rules than undermining the system on which their own wealth
depends.

## 7. Competing chains

Two nodes may find a block at almost the same time, or a group of nodes may have been disconnected and mining
on its own. Two versions of the chain then exist. Nodes keep working on the first one they received, but keep
the other in case it overtakes it. The tie is broken when the next block appears: the branch with **the most
accumulated work** becomes the valid one on all nodes.

Chains are not merged. The blocks of the losing branch are discarded and, with them, their rewards. The
transactions they contained become pending again if they are still valid. A block mined according to the
rules keeps its reward as long as it remains in the chain with the most work. That is why it is advisable to
wait for a few confirmations before treating a large payment as final, and to mine with the network
connected: an isolated node can mine valid blocks that later lose against the chain of everyone else.

There are two protection rules:

- **Maximum reorganization depth:** a node does not replace more than 288 already accepted blocks (about two
  days).
- **Checkpoints:** the published program includes the hashes of some blocks already buried in the history.

Both limit the damage of a long attack, in exchange for a little trust in the software being downloaded.
They are a deliberate and known design decision.

## 8. Privacy

The traditional banking model achieves privacy by restricting access to information. In SHENT, as in any
public record, all transactions are visible: addresses, amounts and times. Privacy is preserved in another
way: addresses have no names.

Measures it includes:

- **New addresses for receiving.** The wallet generates a new address every time "Receive" is requested.
  They all come from the same backup, but seen from outside they have no relation to each other… until they
  are used together in a payment.
- **Dandelion++.** One's own transactions first travel along a random path of nodes and only then are
  broadcast, so that it is not evident from which IP they came.
- **Tor.** The node can connect only through a SOCKS5 proxy. In that mode it does not listen for connections
  and does not use the DHT, the local network or UPnP.

On encryption, to avoid misunderstandings:

- **Private keys are encrypted**, only on each person's computer.
- **The blockchain is not encrypted**, on purpose. Every node must be able to read and verify every
  transaction. Blocks, transfers and balances are shared almost in real time between nodes, in the clear.
  They cannot be forged because they are signed and sealed with work, not because they are hidden.
- **The P2P protocol is not encrypted either**, just like Bitcoin's. Anyone observing the connection can see
  that the computer runs SHENT and which public data passes through it. To hide the IP there is Tor mode.

## 9. Wallet and backup

Keys are generated on the computer itself with the operating system's random generator. They are stored in a
`.shent` file, encrypted with a master password (Argon2id + ChaCha20-Poly1305). The private key never leaves
that computer and is never sent over the network.

- Without the file and without the password, the coins cannot be recovered. There is no recovery mechanism:
  no one can reset the password.
- A copy of the `.shent` file must be kept off the computer (a USB drive, a cloud service) and the password
  in a safe place.
- The file, the password and the key must not be shared with anyone. No developer or node will ever ask for
  them.

## 10. Security and limits

An attacker with more computing power than the rest of the network could:

- rewrite recent blocks,
- reverse their own transactions (double-spending),
- prevent other people's transactions from being confirmed.

On the other hand, they could not:

- create coins out of nothing,
- spend coins from an account whose key they do not have,
- change the rules.

Honest nodes reject any invalid block, wherever it comes from.

Security depends on how much honest power there is. In a young network, with few miners, a single powerful
computer can hold the majority. The application's Statistics page shows how many addresses have mined in
recent days and warns when a single one concentrates more than half of the blocks.

This software is provided as is, without warranty. No system is invulnerable. The rules, the code and the
tests are in plain view for anyone to review.

## 11. Practical use

### Install (Windows 10/11, 64-bit)

1. Download `SHENT-<version>-win-x64.zip` from the *Releases* page of the repository.
2. Verify the hash with `Get-FileHash .\SHENT-<version>-win-x64.zip -Algorithm SHA256` and compare it
   with the published `.sha256` file.
3. Extract the entire zip and open `SHENT.exe`. Nothing else needs to be installed.
4. If Windows Firewall asks, allow access so that other nodes can connect.

### First steps

- **Wallet → Create wallet.** Choose a master password and save the `.shent` backup.
- **Mining.** Choose threads and intensity and fire up the reactor. If there are no connected peers, the
  application warns about it: what is mined without a network may be discarded.
- **Send.** Paste the destination address, the amount and the fee. Review the confirmation screen before
  signing.
- **Receive.** Share the address shown. It is public and does not allow anything to be spent.
- **Language.** The globe selector switches the interface between 10 languages. Amounts are written with the
  decimal separator of the chosen language. If an amount is ambiguous, the application rejects it instead of
  guessing.

### Making the network stronger

Open **TCP port 37373** on the router, forwarded to the computer, or enable UPnP. Each node that accepts
connections makes it easier for others to join. For a headless server there is the console node:
`nodo-consola\shent-node.exe help`.

### Building from source

Requires the .NET 10 SDK.

```
dotnet build Shent.sln
dotnet test tests/Shent.Core.Tests
dotnet run --project src/Shent.Desktop
```

The Windows release is generated with `scripts/publish-windows.ps1`. The technical documentation of the
network is in [`docs/P2P_INTERNET.md`](../P2P_INTERNET.md).

## 12. Conclusion

We have proposed a system for electronic transactions that does not rely on trust. Digital signatures give
control of the coins to whoever holds the key. To prevent double-spending, the network records the public
history of transactions in a proof-of-work chain, which becomes impossible for an attacker to alter as long
as honest nodes hold most of the computing power. The network is robust in its simplicity: nodes work all at
once with very little coordination, do not need to be identified, and can leave and rejoin at will,
accepting the chain with the most work as proof of what happened. They vote with their computing power: they
accept valid blocks by working on them and reject invalid ones by refusing to do so. All the necessary rules
and incentives are enforced with this single consensus mechanism.

SHENT exists for a community to use, study and improve.
