# Contributing to RelayOS

RelayOS is an early experiment in delay-tolerant encrypted relay. Contributions are welcome, but changes must preserve the distinction between the in-process simulator and real device networking.

## Development setup

Install the .NET 10 SDK selected by `global.json`, then restore and test the Core project from the repository root:

```bash
dotnet restore tests/RelayOS.Core.Tests/RelayOS.Core.Tests.csproj
dotnet build src/RelayOS.Core/RelayOS.Core.csproj --configuration Release --no-restore
dotnet test tests/RelayOS.Core.Tests/RelayOS.Core.Tests.csproj --configuration Release --no-restore
```

These commands do not require a .NET MAUI workload. To work on the Android demo, also restore the workload required by `src/RelayOS.Demo/RelayOS.Demo.csproj` and use an Android emulator or device.

## Proposing a change

1. Open an issue or discussion for a protocol, storage-format, transport-contract, or cryptography change before investing in a large implementation.
2. Keep each pull request focused and explain the user-visible behavior, compatibility impact, and security assumptions.
3. Add or update tests for successful behavior, invalid input, cancellation, persistence, and failure cases as applicable.
4. Update the README when a limit, public API, protocol field, setup command, or feature status changes.
5. Run the Core tests locally before requesting review.

## Code expectations

- Target .NET 10 and keep nullable reference types enabled.
- Prefer cancellation-aware asynchronous APIs for storage and transport operations.
- Treat `RelayPacket` instances received from another node or loaded from disk as hostile input.
- Bound input sizes and resource use before allocation or persistence.
- Avoid logging plaintext payloads, private keys, derived keys, nonces with payload context, or complete queue documents.
- Keep Core independent of MAUI and platform-specific APIs.
- Preserve defensive copying of mutable byte arrays at public boundaries.
- Do not add a production dependency without documenting why it is needed and how it behaves on Android.

## Cryptography and protocol changes

Cryptographic changes need dedicated tests and careful review. Do not introduce custom primitives or silently change authenticated fields, byte encoding, key derivation context, nonce size, tag size, or protocol-version behavior. A proposal should include:

- the threat or interoperability requirement being addressed;
- a reference to the standard construction being used;
- key lifecycle and trust assumptions;
- compatibility and migration behavior;
- test vectors or deterministic fixtures where appropriate;
- negative tests for tampering, wrong keys, malformed inputs, and boundary sizes.

Passing tests is not a substitute for independent security review. Do not describe RelayOS as audited or production-ready without evidence.

## Transport contributions

`LocalRelayTransport` is intentionally only an in-process simulator. A physical transport proposal must account for permissions, discovery, identity binding, framing, flow control, cancellation, device lifecycle, background restrictions, abuse limits, and platform policy.

Documentation and UI must identify simulated behavior clearly. Do not call an adapter a mesh implementation until multi-device discovery, transfer, relaying, lifecycle behavior, and relevant threat handling are implemented and tested on real devices.

## Tests

At minimum, preserve coverage for:

- encrypt/decrypt round trips and authentication failures;
- TTL boundaries and expiry pruning;
- priority ordering;
- duplicate and conflicting packet IDs;
- persistence across store instances;
- disconnected and multi-hop local-simulator scenarios;
- invalid or corrupted packet and queue data.

Use temporary directories for storage tests and ensure they are cleaned up. Keep tests deterministic by injecting a `TimeProvider` where time affects behavior.

## Commits and pull requests

Use concise, imperative commit messages. In the pull request description, include what changed, how it was tested, and any remaining limitations. Never include credentials, exported private keys, user data, generated build output, or device-specific signing files.

By contributing, you agree that your contribution is licensed under the repository's MIT License.
