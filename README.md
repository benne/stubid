# StubID

A stand-in for the test environments of the Danish MitID identity brokers, so you can run
your MitID login and signing integration in automated tests.

**Status: early development.** A login works: a stock ASP.NET Core application signs in
against it, and so does Node's `openid-client` — over TLS as well as plain HTTP, each trusting
only the certificate the instance hands out, with nothing relaxed on either side. So does a real
browser over TLS, in Chromium, Firefox and WebKit, which is the step the real broker makes
impossible. Spring Security resolves its metadata from the path-bearing issuer, which is the part
it is strictest about. Tests can create citizens, decide how each login resolves, and move the
clock to force a timeout, and a .NET suite can drive all of that from code — against a container,
or against an instance hosted inside the test process with no Docker at all. Every instance also
serves pages that show logins arriving, decide them by hand, and say what the build emulates. What
is missing is the documentation site and a 1.0. See
[docs/roadmap.md](https://github.com/benne/stubid/blob/master/docs/roadmap.md).

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

- Approve automatically, from a test, or by clicking.
- Make a login fail with a specific broker error code, on demand and repeatably.
- Move the clock forward to trigger a timeout, without waiting for it.
- Run offline, in CI, in milliseconds.

```csharp
await using var stub = new StubIdBuilder().Build();
await stub.StartAsync();

var citizen = await stub.Citizens.CreateAsync(
    new CitizenSpec { Name = "Anders Berg Christiansen", DateOfBirth = new DateOnly(1985, 3, 29) });

await stub.Behaviour.EnqueueAsync(Decision.Approved(citizen.Id).ForClient(clientId));
// Point the application at stub.Authority and sign in.
```

The container is ready in about three seconds, and the login itself takes about fifty
milliseconds. Both numbers come from a test that runs in CI.

Seeing a login work, against an application you can start yourself, is in
[docs/guides/signing-in.md](https://github.com/benne/stubid/blob/master/docs/guides/signing-in.md).
How a login is decided, and how to ask why it went the way it did, is in
[docs/guides/approvals.md](https://github.com/benne/stubid/blob/master/docs/guides/approvals.md). Running StubID from a test suite is in
[docs/guides/testcontainers.md](https://github.com/benne/stubid/blob/master/docs/guides/testcontainers.md) for the container, and in
[docs/guides/in-process.md](https://github.com/benne/stubid/blob/master/docs/guides/in-process.md) for a host inside the test process, which
starts in about 150 milliseconds and needs no Docker. Trusting the certificate it serves over TLS,
from any stack, is in [docs/guides/certificates.md](https://github.com/benne/stubid/blob/master/docs/guides/certificates.md), and driving a
login from a browser test is in [docs/guides/browsers.md](https://github.com/benne/stubid/blob/master/docs/guides/browsers.md).
Watching logins arrive and steering an instance by hand, without writing any code, is in
[docs/guides/admin.md](https://github.com/benne/stubid/blob/master/docs/guides/admin.md) — which is
also where the exposure this tool assumes is written down.

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
established is written up in [docs/brokers/neb/claims.md](https://github.com/benne/stubid/blob/master/docs/brokers/neb/claims.md).

## Not affiliated

StubID is an independent project. It is not affiliated with, endorsed by, or connected to
Digitaliseringsstyrelsen, Signaturgruppen, Idura, or IN Groupe. It performs no
authentication, verifies no identity, and produces no signature with any legal effect. Do
not point it at real people or real personal data. See [NOTICE](https://github.com/benne/stubid/blob/master/NOTICE) and
[TRADEMARKS.md](https://github.com/benne/stubid/blob/master/TRADEMARKS.md).

## Licence

Apache-2.0. See [LICENSE](https://github.com/benne/stubid/blob/master/LICENSE).
