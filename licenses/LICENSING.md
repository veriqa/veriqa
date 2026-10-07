# Veriqa Licensing

## License map

- Veriqa Core — the server core (`Veriqa.Core.TransactionEngine`,
  `Veriqa.Core.ChannelAdapter`, `Veriqa.Core.AuthServer`), its satellite
  packages (`Veriqa.Core.Configuration`, `Veriqa.Core.BaseChannels`, the
  channel adapters, the audit trail, the EF Core / Redis / RabbitMQ / PostgreSQL
  migration packages), and the standalone `Veriqa.Core.AuthServer.Host` image —
  [Mozilla Public License 2.0](../LICENSE).
- `Veriqa.Core.Contracts`, `Veriqa.ServiceDefaults` and the samples —
  [MIT](./LICENSE-MIT).
- Veriqa Cloud (console, multi-tenancy) — Elastic License 2.0, distributed
  separately; see [COMMERCIAL-TERMS.md](./COMMERCIAL-TERMS.md).
- The Veriqa name and logo — [TRADEMARK.md](./TRADEMARK.md).
- Third-party components — [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md).
- Third-party product names and logos shown in the documentation and the demo
  media — [TRADEMARK.md](./TRADEMARK.md#third-party-marks).

Every source file carries an SPDX header naming its license. A file in this
repository without a header is MPL-2.0, except files under
`Veriqa.Core.Contracts`, `Veriqa.ServiceDefaults` and `samples/`, which are
MIT.

There are no revenue thresholds, no user limits, and no license key in the core.

## Licensing FAQ

**Can I use Veriqa in my commercial SaaS?**
Yes. MPL-2.0 permits commercial use, in production, without thresholds — for
your own product, for your customers' products, or as part of a hosting offer.

**What does MPL-2.0 require from me?**
Keep the license and copyright notices in the files. If you modify files of
Veriqa itself and distribute them (for example, ship a patched image), make
the modified files available under MPL-2.0. If you redistribute Veriqa in
binary form, modified or not — an image, a package, an installer — tell the
recipients where the source is (MPL-2.0 §3.2); a link to this repository is
enough. Your own code stays under the license you choose as long as it lives
in your own files (MPL-2.0 §3.3): the copyleft is per file, so code you add to
a Veriqa file is covered, code in your own files is not. Talking to the Docker
image over HTTP requires nothing at all. This is a plain-language summary —
the license text is authoritative.

**Can I fork Veriqa?**
Yes — the code is open source. The **name and logo are not**: a fork ships
under its own name and without the Veriqa logo. See
[TRADEMARK.md](./TRADEMARK.md).

**What is the "Powered by Veriqa" attribution?**
The sign-in and confirmation pages rendered by Veriqa Core show a small
"Powered by Veriqa" attribution by default. Displaying it unmodified is a use
of our marks that [TRADEMARK.md](./TRADEMARK.md) permits. The license does not
require you to keep it — MPL-2.0 lets you change the code. What the commercial
tier adds is an officially supported white-label build under a signed
agreement — see [COMMERCIAL-TERMS.md](./COMMERCIAL-TERMS.md).

**What is commercial, then?**
White-label builds, the Veriqa Cloud console (multi-tenancy, per-tenant
channel credentials, secrets isolation), any proprietary add-on packages
distributed only under a commercial agreement, and vendor support with
contractual terms. Veriqa Cloud is a separate distribution under the Elastic
License 2.0 with a license key; its community tier is free and single-tenant.
See [COMMERCIAL-TERMS.md](./COMMERCIAL-TERMS.md).

**Does the core phone home or check a key?**
No. There is no license key and no telemetry in Veriqa Core.

## AI-assisted development

Veriqa is designed, specified and reviewed by its maintainers. AI coding
tools are used in implementation under human direction; every change is
specified before it is written and reviewed and accepted by a person before
it ships. Contributors are asked to follow the same rule — see
[CONTRIBUTING.md](../CONTRIBUTING.md). If you spot something odd, please open
an issue.
