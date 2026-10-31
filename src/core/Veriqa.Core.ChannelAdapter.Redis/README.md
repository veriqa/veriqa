# Veriqa.Core.ChannelAdapter.Redis

**Run the Veriqa channels on several replicas of your host.** A magic link, a chat prompt, a
shared phone number or a start code issued by one replica keeps working on another.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Channel stores: multiple replicas](https://veriqa.app/docs/guides/storage#channel-stores-multiple-replicas).

## About this package

The package replaces the in-process defaults of the channel stores with Redis ones: the
coordinates of a sent prompt, the phone number requests sent into chats, the link handles put into
post-completion buttons, the short codes a sign-in is started by, Email action tokens, Email push
correlations, the store of the email completion (the codes, links and email counters of the claims
completion) and the state of the phone pages of the provider channels (sign-in with a Google, Apple
or other provider account). With them in Redis, a magic link opened against another replica of the
host still resolves, a prompt sent from one replica is still editable from another, a shared phone number finds
its sign-in whichever replica receives it, a link handle issued by one replica is exchanged by
another, a start code shown by one replica starts the sign-in on another, and a code of the email
completion sent by one replica is checked by another. A phone page of a provider channel shows its
question and messages whichever replica serves it.

Redis stays out of the channel contour's dependency graph: a deployment that runs one replica never
receives a Redis client. The package itself references the Email package and the provider channel
package `Veriqa.Core.ChannelAdapter.WebProviders`, with its OpenIddict client, because it implements
their stores.

## Install

```bash
dotnet add package Veriqa.Core.ChannelAdapter.Redis
```

## Wiring

```csharp
authServer.AddChannelAdapters(adapters =>
{
    adapters.AddEmail();
    adapters.UseRedisChannelStores(redis => redis.Configuration = "localhost:6379");
});
```

The stores are registered unconditionally, so the call works before or after `AddEmail()`.

Every store keeps its keys under a prefix of its own, set on `RedisChannelStoreOptions` — for the
link handles it is `LinkHandleKeyPrefix` (`veriqa:ch:link:` by default); what follows the prefix is a
fingerprint of the handle, never the handle itself. The state of the phone pages of the provider
channels is under `WebProviderStateKeyPrefix` (`veriqa:ch:webprovider:` by default).

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
