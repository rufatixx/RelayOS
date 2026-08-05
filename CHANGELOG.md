# Changelog

All notable RelayOS changes are recorded here. The project follows Semantic
Versioning for releases; the packet protocol has its own version field.

## [0.1.0-alpha.1] - 2026-08-05

### Added

- Recipient-key payload encryption using ephemeral P-256 ECDH, HKDF-SHA-256,
  and AES-256-GCM.
- Durable JSON packet queues with atomic replacement, TTL pruning, priority
  ordering, deduplication, and conflicting-ID detection.
- Transport contracts and an in-process `LocalRelayHub` simulator.
- Alice → Courier → Bob store-carry-forward tests and console simulation.
- .NET MAUI Android source demo for the same local simulation.
- Contributor, governance, security, trademark, and project identity policies.

### Known limitations

- No physical device transport, peer discovery, background operation, or
  production routing.
- No sender signatures, key-discovery system, or delivery receipts.
- No independent security audit or Android release verification.

[0.1.0-alpha.1]: https://github.com/rufatixx/RelayOS/releases/tag/v0.1.0-alpha.1
