# StubID

A stand-in for the Danish MitID identity brokers' test environments, so an integration that signs
users in with MitID can be tested without one.

What it emulates is checked byte for byte against recordings of the real broker, including the
parts that look like mistakes. Where it knowingly differs it says so, in the ledger a running
instance serves and in [the divergences](brokers/neb/divergences.md).

## Start here

The shortest path from nothing to a page of claims is [signing in](guides/signing-in.md). It
routes you to whichever of the four client stacks is yours, and it starts with a `docker run` line
you can paste.

## Running it from a test suite

| | |
| --- | --- |
| [From a test suite](guides/testcontainers.md) | `StubId.Testing`, which starts a container and hands the address to the application under test |
| [Inside the test process](guides/in-process.md) | `StubId.InProcess`, which needs no Docker at all |
| [Deciding what a login does](guides/approvals.md) | how an outcome is chosen, why it was what it was, and what a timeout looks like |
| [Watching an instance](guides/admin.md) | the pages every instance serves, and why there is no password on them |
| [Trusting the certificate](guides/certificates.md) | per stack, when an instance serves TLS |
| [Driving a real browser](guides/browsers.md) | Chromium, Firefox and WebKit, which the real broker makes impossible |

## What the broker actually does

Written from recordings rather than from the vendor's documentation, because the two disagreed
often and the recording won every time. [The broker reference](brokers/neb/index.md) has the
tokens, the refusals, the request parameters, and every place StubID differs on purpose.

## Why things are the way they are

[CPR numbers and test data](explanation/cpr-and-test-data.md) explains why the personal numbers
here belong to nobody by construction. [The profile seam](explanation/profile-seam.md) explains
what is behind the broker abstraction and what deliberately is not yet.

The [research notes](research/day-zero-probes.md) are the measurements the reference is built on,
dated and kept as written. [The capture session](capture-session.md) is the runbook for taking a
new recording.

## The API

[The reference](api/index.md) covers the four packages a reader installs: `StubId.Client`,
`StubId.Testing`, `StubId.InProcess` and `StubId.Abstractions`.

## Where it is

[The roadmap](roadmap.md) says what is done and what is not. [The releases](releases/2026.09.2.md)
say what changed, and the version is a date for the reason the first one gives.

StubID is not affiliated with, endorsed by, or connected to Nets, Signaturgruppen,
Digitaliseringsstyrelsen or MitID A/S. It emulates a published protocol surface for testing.
