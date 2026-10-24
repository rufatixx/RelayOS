# RelayOS Delivery Lab

The Delivery Lab is a local, interactive demonstration for developers evaluating
the Core SDK. Its scenario is a harmless field note moving from **Field Station**
through **Courier** to **Research Desk**. All nodes and contacts live in one server
process on your computer. It does not connect phones or use radios.

## Start

From the repository root, with the .NET SDK selected by `global.json`:

```bash
dotnet run --project samples/RelayOS.DeliveryLab
```

Open http://localhost:5088. Stop the server with Ctrl+C. You need only the .NET SDK
and a browser; there is no JavaScript build, CDN, account, or MAUI workload.

## What to observe

| Step | Station pending | Courier pending | Desk delivered records | Evidence |
| --- | ---: | ---: | ---: | --- |
| Queue note | 1 | 0 | 0 | Actual encrypted packet saved in Station's file queue |
| Try with no contact | 1 | 0 | 0 | No connected peers, no transfer |
| Pick up note | 1 | 1 | 0 | Only Station and Courier meet; SDK rejects Courier as the non-recipient |
| Reopen queue | 1 | 1 | 0 | A new file-store instance reloads the same packet ID |
| Deliver note | 1 | 1 | 1 | Only Courier and Desk meet; Desk shows the exact original note |
| Retry delivery | 1 | 1 | 1 | Desk's duplicate counter increases, delivery count stays one |

Every encounter ends by disconnecting the simulated peers. There is no direct
Station-to-Desk link. Repeating pickup or delivery is safe for this scenario:
Core's receiving queue detects the duplicate packet. This is not a general
exactly-once application-delivery guarantee.

The queue counts come from `FileRelayPacketStore.GetStatisticsAsync`. The event
log uses the actual forward/receive reports. Reopening validates the packet ID
read back from disk. The sample does not alter Core or add a wire format.

## Try a failure case

1. Reset the run and queue a fresh note.
2. Let Courier pick it up, but do not deliver it.
3. Advance the simulated clock by 61 minutes.

The one-hour TTL has elapsed. All queue copies are pruned, the state becomes
**Expired before delivery**, and Research Desk has no note. A browser refresh
does not restore expired records. Reset to try again.

If you advance the clock *after* delivery, delivered queue records also expire.
The recipient view keeps the already-read note and the historical delivery count
until reset. Expiry does not retract plaintext already read by an application.

The lab clock does not tick with wall time. The button advances the same injected
`TimeProvider` used by every node and store. This makes expiry easy to reproduce;
it does not test clock skew or independent device clocks.

## State, reloads, and cleanup

- One note per run, at most 1,024 UTF-8 bytes. Multibyte characters may reach the
  limit before 1,024 characters. Whitespace-only notes are rejected.
- All browser tabs share the same server-side run. Reload reads its current
  state. Reset creates a new run ID, clears the queues, and replaces the keys.
  An old tab cannot apply actions to a reset run; it refreshes and explains why.
- Actions are serialized. The UI disables controls while a request is pending.
  Once accepted by the server, a step completes even if a browser reload or
  disconnect interrupts its response. Use **Refresh state** before retrying an
  uncertain request. The lab does not retry mutations automatically.
- The latest 12 events are retained. Payload text is shown only in the note input
  and recipient view, not interpolated into HTML or logged in the event history.
- Queue files live in a unique directory under your OS temporary directory's
  `relayos-delivery-lab` folder. Normal shutdown removes this run's directory.
  A crash, forced kill, or cleanup error can leave ciphertext and visible routing
  metadata behind. The sample does not persist its private keys, so it cannot
  resume a run after server restart. Do not enter sensitive data.
- **Reopen queue** closes and recreates Courier's file-store instance. Identities,
  transport objects, and the server remain alive. It is not a crash-recovery test.

## Local-only boundaries

The server binds to `localhost:5088`, rejects non-loopback hosts, limits request
bodies to 8 KiB, and requires JSON plus a same-origin custom header for mutations.
Browser text is rendered with `textContent`, not HTML. Assets are local and a
Content Security Policy excludes external scripts, styles, frames, and requests.
These are sample-level safeguards, not a production security assessment. Do not
expose the lab through a tunnel, reverse proxy, or public server.

Recipient-key encryption does not authenticate the claimed sender. Key exchange,
secure key storage, real transport, routing, receipts, and independent protocol
review are outside this demo. Read the main [security boundaries](../README.md#cryptography).

## Troubleshooting

- **SDK not found / incompatible SDK:** install SDK `10.0.201` or a newer patch in
  that feature band. `dotnet --list-sdks` shows installed versions. The runtime
  alone is not enough.
- **Address already in use:** stop the other process using port 5088, or reuse
  the already-running lab. The sample intentionally does not accept a public
  bind address through `--urls`.
- **Page cannot connect:** keep the `dotnet run` terminal open and use
  http://localhost:5088 on the same computer. This is not a hosted website.
- **Another tab reset the run:** refresh state and start a new note if desired.
- **Note expired:** reset to start again. Expiry cannot be undone in this run.

## Verify changes

```bash
dotnet test tests/RelayOS.Core.Tests --configuration Release
dotnet test tests/RelayOS.DeliveryLab.Tests --configuration Release
python3 tests/RelayOS.DeliveryLab.Tests/http_smoke.py
```

The Delivery Lab tests cover offline queues, real ciphertext persistence, reopening,
exact text delivery, duplicates, expiry, reset, stale and concurrent actions, input
limits, cancellation before execution, and temporary-directory cleanup. Core CI
also builds and tests this sample without a MAUI workload.

The optional Python 3 standard-library smoke test runs on Linux/macOS (it uses
POSIX signals for graceful shutdown). It starts its own server on port
5088, checks the HTTP boundary and full delivery workflow, then verifies graceful
shutdown cleanup. Stop any existing lab before running it. This check is included
in CI; Python is not required to run the lab itself. It does not replace visual
browser testing.
