# Contributing to Veriqa

Thank you for your interest in contributing to Veriqa! Veriqa Core is **open
source** under the Mozilla Public License 2.0 (see [LICENSE](./LICENSE)); a
few peripheral packages are MIT ([LICENSE-MIT](./licenses/LICENSE-MIT)). The license
map — which package is under which license — is in
[LICENSING.md](./licenses/LICENSING.md).

## Contributor License Agreement (CLA)

Before we can merge your first pull request in any Veriqa repository you must
accept our CLA once: the [Individual CLA](https://veriqa.app/cla/individual)
if you contribute on your own behalf, the
[Corporate CLA](https://veriqa.app/cla/corporate) if you contribute for your
employer. One acceptance covers all Veriqa repositories. The CLA grants the
licensor and its successors the rights needed to distribute your contribution
under MPL-2.0, under the Elastic License 2.0 (Veriqa Cloud) and under
commercial terms; you keep ownership of your work.

How to accept: send an email to `devs@veriqa.app` from the address you commit
with, stating "I accept the Veriqa Individual CLA, version X.Y" together with
your full name and your code-hosting account name. A corporate representative
sends the same statement for the Corporate CLA with the completed Schedule A.
We confirm by reply and record the acceptance; a pull request is not merged
until the acceptance is on record.

- Any change submitted to a Veriqa repository is a Contribution under the CLA —
  including changes to MIT-licensed files, documentation and samples.
- Code that lives in your own repository — an adapter built against
  `Veriqa.Core.Contracts`, an integration, a fork — is not a Contribution and
  needs no CLA.
- Why a CLA and not a DCO: the Veriqa model (an open-source core next to a
  commercial tier and a source-available cloud distribution) requires the
  ability to relicense contributed code, which a DCO does not provide.

## Building your own channel adapter? No CLA needed

Third-party channel adapters built against the MIT-licensed
`Veriqa.Core.Contracts` package live in **your own repository**, under the
license of your choice, and do **not** require accepting our CLA. This is the
intended ecosystem path: implement the adapter interfaces from the Contracts
package and ship independently. We are happy to link community adapters from
the documentation — open an issue to tell us about yours. How you may name
such a package is described in [TRADEMARK.md](./licenses/TRADEMARK.md).

## How to contribute

1. **Open an issue first** for anything non-trivial (bug report, feature
   proposal) so we can discuss the approach before you invest time.
2. **Fork and branch**: create a feature branch from the default branch.
3. **Keep changes focused**: one logical change per PR.
4. **Build must be green**: the CI pipeline (build + analyzers + license
   gate) must pass. Analyzer errors are blocking by design.
5. **Do not change contract literals**: claim names, configuration keys
   (`Veriqa:*`, including the channel sections under `Veriqa:Channels:*`),
   scenario codes, and rate-limit policy names are public API surface.
6. Open the PR; make sure your CLA acceptance is on record (see above);
   address review feedback.

## AI-assisted contributions

You may use AI coding tools. You remain the author of what you submit: you
must have read, understood and tested it, you disclose in the pull request
that AI tools were used, and you must not submit output that reproduces
third-party code under an incompatible license. The CLA representations
apply to AI-assisted contributions in full.

## Security issues

Do **not** open public issues for vulnerabilities — see
[SECURITY.md](./SECURITY.md) for the responsible disclosure process.

## Code of Conduct

This project follows the [Contributor Covenant Code of
Conduct](./CODE_OF_CONDUCT.md). By participating, you agree to abide by it.
