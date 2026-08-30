# Nets eID Broker: where StubID differs on purpose

Every entry here is a deliberate decision, not an omission. A running instance serves the same
list at `GET /_stubid/v1/fidelity`, read from annotations next to the code that emits each
behaviour, so this document and the running system cannot disagree.

## Client secrets are not checked

<a id="client-secrets"></a>

The broker refuses a wrong secret with `{"error":"invalid_client"}`. StubID accepts any
non-empty secret for a registered client.

**Why.** A stub cannot know the secret an existing configuration already carries, and
demanding a particular one would defeat the point of changing only the authority. A missing
secret is still refused, because telling "authenticated badly" from "did not authenticate
at all" is behaviour worth keeping.

**What this costs.** A test asserting that a wrong secret is rejected passes against
pre-production and fails here. If that matters to you, say so — pinning expected secrets per
client is a small change.

## Advertised but not implemented

The discovery document is served from a recording, so it advertises everything the broker
does. Some of that is not implemented:

| Advertised | State |
| --- | --- |
| `backchannel_authentication_endpoint` (CIBA) | not implemented; the endpoint 404s |
| `end_session_endpoint` | not implemented; the endpoint 404s |
| Request objects (`request`, `request_uri` parameters) | not implemented |
| Request-object encryption | not implemented |
| DPoP | not implemented |

Trimming the discovery document to match would be *less* faithful, not more: some client
libraries key off metadata that is absent, and the recording is what the broker sends. The
honest position is to advertise what the broker advertises and say plainly what is missing.

## Transaction signing

The transaction token's text claims are unrecorded, because they need the `signtext_api`
scope, which only the broker's staff can grant. Nothing is emitted for them rather than
guessing: the broker's own documentation contradicts itself three ways on their names.

## The OCES3 certificate chain

The broker signs its transaction token with a certificate issued by a Danish state CA, and
returns an OCSP response alongside it. StubID signs with its own certificate.

A client that resolves the signing key by `kid` from the published key set — which is what the
broker's own verification guide tells you to do — works against both. A client that validates
the certificate *chain*, or the OCSP response, works against pre-production and fails here.
There is no fix for that; it is disclosed rather than papered over.

## What is not reproduced

Infrastructure that belongs to the broker's hosting rather than its protocol: the `server`
header, `x-neb-site`, HSTS and CSP headers, the wording of the error page, and response
timing. StubID emits an `X-StubID-Emulator` header of its own so an instance cannot be
mistaken for the real thing.
