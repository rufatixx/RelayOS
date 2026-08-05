# RelayOS.Core

RelayOS.Core is an experimental .NET 10 SDK for recipient-encrypted,
delay-tolerant store-carry-forward messaging.

The alpha contains packet cryptography, durable file queues, TTL, priorities,
deduplication, transport contracts, and an in-process local simulator. It does
not contain Bluetooth, Wi-Fi Direct, mesh networking, background discovery, or
production routing.

## Start here

Run the repository's complete three-node example:

```bash
dotnet run --project samples/RelayOS.Simulator -- "Hello through a relay"
```

Read the [full documentation](https://github.com/rufatixx/RelayOS#readme),
[security boundaries](https://github.com/rufatixx/RelayOS#cryptography), and
[release notes](https://github.com/rufatixx/RelayOS/releases).

RelayOS has not received an independent security audit. Do not use this alpha
for emergencies or other safety-critical communication.
