# RelayOS

[![Core](https://github.com/rufatixx/RelayOS/actions/workflows/core.yml/badge.svg)](https://github.com/rufatixx/RelayOS/actions/workflows/core.yml)
[![License: AGPL-3.0-only](https://img.shields.io/badge/license-AGPL--3.0--only-blue.svg)](LICENSE)
[![Status: MVP](https://img.shields.io/badge/status-MVP-orange.svg)](#status-and-safety)

RelayOS is an experimental .NET 10 foundation for encrypted, delay-tolerant message relay: a way for data to move through nearby carriers even when there is no normal network path.

The project is built around a simple idea: a node writes an encrypted packet to a local queue, hands copies to peers it can currently reach, and another node can carry the packet until it encounters the recipient. Relays see routing metadata, not the protected payload.

This repository is an MVP. Its transport is an in-process simulator: **there is no Bluetooth transport, Wi-Fi Direct transport, mesh networking, background discovery, production routing, or delivery-receipt protocol.**

## Why it matters

Most communication software assumes that the internet exists, stays reachable, and stays affordable. RelayOS explores the opposite condition: devices that meet briefly, carry data forward, and sync opportunistically.

The long-term direction is an open, inspectable protocol for offline-first communication experiments, disaster-resilient coordination, field research, campus/local communities, and privacy-conscious apps that need more than a cloud queue. The current code is intentionally small, testable, and honest about what is not done yet.

## Project principles

- Security claims must be earned by tests, review, and clear threat models.
- The simulator must never be marketed as real radio networking.
- Public APIs should stay boring, predictable, and hard to misuse.
- RelayOS should be useful to builders without letting copycats impersonate the official project.
- Contributors get visible credit for meaningful work.

## What works

- `RelayOS.Core`, targeting `net10.0`
- end-to-end payload encryption using .NET cryptography APIs
- a file-backed JSON queue with atomic replacement on commit
- TTL enforcement, four priority levels, and packet-ID deduplication
- a transport abstraction for future adapters
- `LocalRelayHub`, which simulates explicit peer encounters in one process
- a three-node store-carry-forward scenario: Alice → Courier → Bob
- Core tests that do not require a MAUI workload
- a .NET MAUI demo project for showing the local simulation on Android

## Status and safety

RelayOS is a technical demonstration, not a production communications system. It has not received an independent security review. Do not depend on this MVP for emergencies, medical care, public safety, military use, or any situation in which delayed, duplicated, disclosed, or lost data could cause harm.

## Architecture

```text
Application / MAUI demo
          │
          ▼
      RelayNode ─────────────── RelayIdentity / RelayCryptography
          │                                  │
          ├── IRelayPacketStore              └── encrypted RelayPacket
          │      └── FileRelayPacketStore
          │
          └── IRelayTransport
                 └── LocalRelayTransport ─── LocalRelayHub
                                              (same process only)
```

The main components are:

| Component | Responsibility |
| --- | --- |
| `RelayNode` | Creates, queues, forwards, accepts, and decrypts packets. |
| `RelayPacket` | Carries public routing metadata and encrypted payload fields. |
| `RelayIdentity` | Owns a node's P-256 ECDH private key and exposes its public key. |
| `RelayCryptography` | Encrypts for a recipient and decrypts at that recipient. |
| `IRelayPacketStore` | Defines queue, delivery-state, expiry, and statistics operations. |
| `FileRelayPacketStore` | Persists a node's queue as one JSON document. |
| `IRelayTransport` | Defines broadcast and receive operations without choosing a radio. |
| `LocalRelayHub` | Simulates links between nodes created in the same process. |

### Packet lifecycle

1. `RelayNode.SendAsync` encrypts a payload for a supplied `RelayPublicKey` and persists the resulting packet.
2. `ForwardPendingAsync` asks the transport to broadcast pending, unexpired packets to peers that are currently connected.
3. `ReceiveAvailableAsync` validates each packet independently. A relay stores the encrypted packet; the destination authenticates it before durable acceptance.
4. After a simulated contact changes, the carrier broadcasts its queued copy.
5. The recipient decrypts the packet and marks its local record delivered. A pending destination packet left by an interrupted earlier run is recovered from the queue on the next receive pass.

Forwarding does not remove the sender's or carrier's copy. Repeated broadcasts are expected; each receiving store uses the packet ID and an immutable packet digest to reject exact duplicates. Reusing an ID for different packet content is treated as a conflict. Records, including delivered records, are removed when their TTL expires, after which that store no longer remembers the packet ID.

## Protocol limits

| Setting | MVP value |
| --- | --- |
| Default TTL | 12 hours |
| Maximum TTL | 7 days |
| Maximum plaintext payload | 256 KiB |
| Priorities | `Low`, `Normal`, `High`, `Critical` |
| Queue order | Priority descending, then creation time, then packet ID |
| Default content type | `application/octet-stream` |

TTL is evaluated against each device's local UTC clock. It is an expiry policy, not a guarantee that every copy has been deleted at the same real-world instant.

## Cryptography

For every packet, RelayOS currently uses:

- an ephemeral NIST P-256 ECDH key pair at the sender;
- the recipient's static P-256 ECDH public key;
- HKDF-SHA-256 with a fresh 32-byte salt and a versioned RelayOS context;
- AES-256-GCM with a fresh 12-byte nonce and a 16-byte authentication tag.

The AES-GCM additional authenticated data covers the protocol version, packet ID, sender and recipient IDs, timestamps, priority, and content type. A relay can read this routing metadata but cannot decrypt or silently modify the protected payload. Derived secret material is cleared from managed buffers after use where the .NET APIs permit it.

Important security boundaries:

- The current packet format does **not** include a sender signature. The claimed `SenderId` is integrity-protected after encryption but is not proof of who created the packet.
- Public-key discovery, verification, rotation, revocation, and recovery are outside the MVP. The caller must obtain the correct recipient public key through a trusted channel.
- The Core library can export a PKCS#8 private key, but it does not provide secure key storage. An application must use an appropriate platform keystore.
- Packet payloads are encrypted in the file queue; IDs, endpoints, timestamps, priority, content type, and packet sizes remain visible.
- The SHA-256 queue digest detects inconsistent content for a packet ID and accidental corruption. It is not a keyed file-integrity mechanism against an attacker who can rewrite the queue file.
- This custom protocol has not been audited and does not provide traffic-analysis resistance or forward secrecy after compromise of the recipient's static private key.

## Quick start

Install the .NET 10 SDK. The repository's `global.json` selects SDK `10.0.201` and permits newer patches in that feature band.

Build the Core library and run its tests without installing MAUI:

```bash
dotnet restore tests/RelayOS.Core.Tests/RelayOS.Core.Tests.csproj
dotnet build src/RelayOS.Core/RelayOS.Core.csproj --configuration Release --no-restore
dotnet test tests/RelayOS.Core.Tests/RelayOS.Core.Tests.csproj --configuration Release --no-restore
```

Run these commands from the repository root.

### API example: local store-carry-forward

This example creates three nodes and explicitly changes which simulated peers can meet. Alice and Bob are never connected directly.

```csharp
using System.Text;
using RelayOS.Core;
using RelayOS.Core.Cryptography;
using RelayOS.Core.Models;
using RelayOS.Core.Storage;
using RelayOS.Core.Transport;

var dataDirectory = Path.Combine(Path.GetTempPath(), $"relayos-{Guid.NewGuid():N}");
var hub = new LocalRelayHub();

using var aliceIdentity = RelayIdentity.Create("alice");
using var courierIdentity = RelayIdentity.Create("courier");
using var bobIdentity = RelayIdentity.Create("bob");

using var aliceStore = new FileRelayPacketStore(Path.Combine(dataDirectory, "alice.json"));
using var courierStore = new FileRelayPacketStore(Path.Combine(dataDirectory, "courier.json"));
using var bobStore = new FileRelayPacketStore(Path.Combine(dataDirectory, "bob.json"));

var alice = new RelayNode(aliceIdentity, aliceStore, hub.CreateTransport("alice"));
var courier = new RelayNode(courierIdentity, courierStore, hub.CreateTransport("courier"));
var bob = new RelayNode(bobIdentity, bobStore, hub.CreateTransport("bob"));

await alice.SendAsync(
    bobIdentity.PublicKey,
    Encoding.UTF8.GetBytes("Hello through a carrier"),
    new RelaySendOptions
    {
        TimeToLive = TimeSpan.FromHours(1),
        Priority = RelayPriority.High,
        ContentType = "text/plain; charset=utf-8"
    });

hub.Connect("alice", "courier");
await alice.ForwardPendingAsync();
await courier.ReceiveAvailableAsync(); // encrypted packet enters the carrier queue

hub.Disconnect("alice", "courier");
hub.Connect("courier", "bob");
await courier.ForwardPendingAsync();
var report = await bob.ReceiveAvailableAsync();

var text = Encoding.UTF8.GetString(report.Delivered.Single().Payload);
Console.WriteLine(text);
```

The carrier has no recipient private key, so it cannot decrypt Bob's payload. `Connect` and `Disconnect` only change links inside `LocalRelayHub`; they do not interact with physical radios or the network.

## MAUI Android demo

The MAUI project presents the same Alice → Courier → Bob simulation on one device. All three nodes and every simulated link live inside that single app process. It is a visualization of the Core workflow, not a connection among multiple phones.

Install or restore the Android workload, then build the Android target:

```bash
dotnet workload restore src/RelayOS.Demo/RelayOS.Demo.csproj
dotnet build src/RelayOS.Demo/RelayOS.Demo.csproj --framework net10.0-android
```

Launch it on an Android emulator or connected device from an IDE with .NET MAUI support, or use the platform tooling installed with your MAUI workload. The Core-only CI workflow intentionally does not restore or build the MAUI project.

## Repository layout

```text
src/RelayOS.Core/          Packet model, cryptography, queue, node, transports
src/RelayOS.Demo/          .NET MAUI Android demonstration
tests/RelayOS.Core.Tests/  Core unit and store-carry-forward tests
.github/workflows/core.yml Core-only continuous integration
```

## Honest MVP limitations

The following capabilities are **not implemented**:

- Bluetooth, Bluetooth LE, Wi-Fi Direct, Nearby Connections, or any other physical transport
- mesh discovery, peer negotiation, radio permissions, or background discovery
- Android background services, wake-up scheduling, or operation while the app is suspended
- production routing, path selection, congestion control, backpressure, quotas, or fairness
- hop limits or persistent per-peer forwarding history
- sender authentication, trusted key exchange, a PKI, key rotation, or revocation
- delivery acknowledgements or receipts back to the sender
- multi-process queue locking, database transactions, recovery journals, or encrypted metadata
- protection from malicious peers, replay after deduplication state expires, denial of service, or traffic analysis
- interoperability guarantees across protocol versions

`IRelayTransport` is an extension point, not evidence that a real radio transport exists. The local simulator broadcasts every pending packet to every explicitly connected in-process peer. It should not be described as Bluetooth mesh or offline networking between devices.

## Roadmap

Potential next steps, roughly in dependency order:

1. Specify and independently review a versioned wire protocol and threat model.
2. Add authenticated device identities, signed packets, key verification, rotation, and revocation.
3. Add queue quotas, streaming/chunking, crash recovery, per-peer state, hop limits, and replay-retention rules.
4. Define contact negotiation and capability exchange for transport adapters.
5. Prototype an Android foreground transport using one supported nearby-device API, with explicit permission and lifecycle handling.
6. Add signed delivery receipts and an acknowledgement retention policy.
7. Test interoperability, hostile inputs, long-running relays, clock skew, and large simulated topologies.
8. Commission external protocol and implementation security reviews before any production claim.

Roadmap items are intentions, not current features.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the development workflow and the additional review expected for protocol or cryptography changes.

Good first areas for contributors:

- deterministic tests for hostile packet and queue inputs
- protocol notes, diagrams, and threat-model review
- Android lifecycle research for a future real-device transport
- sample apps that clearly label simulated behavior
- documentation fixes that make the MVP limits easier to understand

## Recognition and project identity

RelayOS is led by [Rufat Asadzade](https://github.com/rufatixx). The project welcomes forks and contributions under the license, but the `RelayOS` name and identity are reserved for the official project and approved community use.

If you build on this code, preserve the license and notices, make your source available when required by AGPL-3.0, and do not present your fork, app, package, account, or service as the official RelayOS project unless you have written permission.

See [NOTICE](NOTICE) and [TRADEMARKS.md](TRADEMARKS.md).

## Security

Please do not open public issues for sensitive vulnerabilities. See [SECURITY.md](SECURITY.md) for the reporting process.

## License

RelayOS source code is available under the [GNU Affero General Public License v3.0 only](LICENSE).

The RelayOS name, logo, marks, and project identity are not included in that license. See [TRADEMARKS.md](TRADEMARKS.md).
