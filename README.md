# StubID

A stand-in for the test environments of the Danish MitID identity brokers, so you can run
your MitID login and signing integration in automated tests.

**Status: early development. Nothing works yet.** The protocol surface is being built
against captured recordings of a real broker. See [docs/roadmap.md](docs/roadmap.md) for
what is done and what is not.

## The problem

If your application signs users in with MitID, you reach it through a broker —
Signaturgruppen ("Nets eID Broker"), Idura, NemLog-in for the public sector. Their
pre-production environments work, but every login has to be approved by hand in MitID's
Test Tool, and the MitID widget blocks browser automation on purpose. A pre-production
login takes 20-30 seconds when the environment is up.

That makes end-to-end tests of a login flow impractical, and it makes the interesting cases
impossible. You cannot ask pre-production for a user who aborts, a session that times out,
a CPR match that runs out of attempts, or a signing key that rotates underneath a cached
metadata document.

## What StubID does

It serves the same endpoints as the broker, on the same paths, with the same claim names
and JSON types, so your application only changes its authority URL and client credentials.
Behind that surface there is no authenticator: you create citizens yourself and decide how
each login resolves.

- Approve automatically, from a test, by clicking, or by a rule on a citizen or group.
- Make a login fail with a specific broker error code, on demand and repeatably.
- Move the clock forward to trigger a timeout, without waiting for it.
- Run offline, in CI, in milliseconds.

## Fidelity

Being close is not useful; a client library either accepts a token or it does not. What the
emulator emits is checked byte-for-byte against recordings of the real broker, including
the parts that look like mistakes. The discovery document omits `scopes_supported`, so ours
omits it too. The broker misspells one `amr` value, so we misspell it identically.

Where StubID knowingly differs, it says so: `GET /_stubid/v1/fidelity` lists every
divergence, and endpoints that are not emulated answer 501 with a link to the reason rather
than a misleading 404.

What that is worth, concretely: the first version of the token was written from the broker's
own documentation and was wrong in eight ways at once. Four of the claims it omitted appear in
no vendor table, one claim it emitted is never sent, and one timestamp is a string where every
other timestamp in the same token is a number. Every one of those tokens validated — a client
library would have accepted all of them. The recordings are in `fixtures/`, and what they
established is written up in [docs/brokers/neb/claims.md](docs/brokers/neb/claims.md).

## Not affiliated

StubID is an independent project. It is not affiliated with, endorsed by, or connected to
Digitaliseringsstyrelsen, Signaturgruppen, Idura, or IN Groupe. It performs no
authentication, verifies no identity, and produces no signature with any legal effect. Do
not point it at real people or real personal data. See [NOTICE](NOTICE) and
[TRADEMARKS.md](TRADEMARKS.md).

## Licence

Apache-2.0. See [LICENSE](LICENSE).
