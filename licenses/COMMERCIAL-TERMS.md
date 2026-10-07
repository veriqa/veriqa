# Veriqa Commercial Terms — overview

This page describes what is offered commercially and how to ask for it. It is
not a license and grants no rights. Commercial rights are granted only by a
signed agreement between you and the licensor. Products described as "in
development" are plans, not offers: availability, features and terms may
change until they ship.

Veriqa Core itself is **open source** under the
[Mozilla Public License 2.0](../LICENSE) — free for everyone, for any purpose,
at any company size, in production, with no revenue thresholds, no user
limits and no license key. The license map (including the MIT-licensed
periphery) and the licensing FAQ are in [LICENSING.md](./LICENSING.md).

## What is commercial, then?

The commercial offering sits *next to* the core, not inside it:

- **White-label.** The sign-in and confirmation pages rendered by Veriqa Core
  show a "Powered by Veriqa" attribution by default. The commercial tier
  provides an official build without it, supported by the vendor. MPL-2.0
  already lets you modify the code; what the commercial tier sells is the
  supported official build and the contractual terms around it, not a
  permission the license withholds. Use of the marks is governed by
  [TRADEMARK.md](./TRADEMARK.md).
- **Veriqa Cloud, self-hosted.** The management console, multi-tenancy
  (projects, per-tenant channel credentials, secrets isolation), and the
  enterprise features around the core ship as a separate distribution under
  the Elastic License 2.0 with a license key. Its community tier is free and
  single-tenant; the key unlocks multi-tenancy and white-label. The
  distribution is in development and is not part of this repository.
  White-label is available either as a term of a commercial agreement for
  Veriqa Core or as a feature enabled by a Veriqa Cloud license key; both are
  obtained through the contact below.
- **Support and contractual terms.** Vendor support with defined response
  targets, warranties, indemnification, audit clauses — whatever your
  procurement needs, negotiated in a signed agreement rather than in this file.

## Do I need a commercial agreement?

- To use Veriqa Core in production — embedded via NuGet or as the Docker image,
  for your own product or for the products of your customers — **no.** MPL-2.0
  covers it.
- To host Veriqa Core for third parties, or to ship a fork — **no.** MPL-2.0
  applies; the name and logo do not come with it (see
  [TRADEMARK.md](./TRADEMARK.md)).
- For an officially supported white-label build, the multi-tenant console, or
  a support contract — **yes**, under a signed agreement.

What MPL-2.0 asks of you is summarised in [LICENSING.md](./LICENSING.md)
("What does MPL-2.0 require from me?"); the license text is authoritative.

## How to get it

Contact: `devs@veriqa.app`

Please include a short description of your deployment (embedded/NuGet or
standalone/Docker, expected scale, single- or multi-tenant) so we can respond
faster.
