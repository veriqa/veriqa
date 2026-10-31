# Veriqa.Core.TransactionEngine.RabbitMq

**Let your other systems react to Veriqa sign-ins and confirmations.** The lifecycle events
of every transaction go to a RabbitMQ broker.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Publishing transaction events](https://veriqa.app/docs/guides/storage#publishing-transaction-events).

## About this package

RabbitMQ publisher of transaction events for the Veriqa Transaction Engine — a satellite
package. The engine publishes the lifecycle events of an authentication transaction in process by
default; this package forwards the same events to a broker, so systems outside the host can react
to them.

It is optional in both directions: a deployment without it loses nothing of the sign-in flow, and
installing it does not change how the in-process subscribers (the live sign-in UI, the audit
journal) see those events.

## Install

```bash
dotnet add package Veriqa.Core.TransactionEngine.RabbitMq
```

## Wiring

```csharp
authServer.ConfigureTransactionEngine(te =>
    te.UseRabbitMqPublisher(rabbit => rabbit.ExchangeName = "veriqa.transactions"));
```

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
