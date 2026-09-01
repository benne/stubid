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

## An id_token_hint is read, not verified

At the end-session endpoint the broker checks the hint it is given. StubID reads it: any
three-part token whose payload carries a `sid` is accepted, including one StubID never issued.

**Why.** The same trade as the client secret above. A test that builds a hint by hand is more
likely than an attack on a stub, and refusing it would fail a test that works.

**What this costs.** A test asserting that a forged hint is rejected passes against
pre-production and fails here.

## Advertised but not implemented

The discovery document is served from a recording, so it advertises everything the broker
does. Some of that is not implemented:

| Advertised | State |
| --- | --- |
| `backchannel_authentication_endpoint` (CIBA) | not implemented; the endpoint 404s |
| `frontchannel_logout_supported`, `backchannel_logout_supported` | ending a session works; notifying the other clients in it does not |
| Request objects (`request` parameter) | not implemented |
| Request-object encryption | not implemented |
| DPoP | not implemented |

Trimming the discovery document to match would be *less* faithful, not more: some client
libraries key off metadata that is absent, and the recording is what the broker sends. The
honest position is to advertise what the broker advertises and say plainly what is missing.

## Where a recording could not settle it

Three behaviours are implemented from the broker's documentation rather than from a
recording, because reaching them needs something the unattended captures cannot do.

| Behaviour | Why it is unrecorded |
| --- | --- |
| End session honouring `post_logout_redirect_uri` with a valid `id_token_hint` | Needs a real id_token, which needs a completed login. The half without a hint *is* recorded, in CAP-044 and CAP-045: the redirect is ignored and the browser goes to the broker's own logout page. |
| The CPR-match refusal after three attempts | Needs a fourth call inside one authenticated session. The sitting that could have recorded it spent its attempts on the earlier branches. The sentence StubID returns is the broker's documented one. |
| `prompt=none` answering `login_required` | Needs a client with single sign-on and a session already open. The specification's answer is used. |
| `cprNumberMatch` being a JSON boolean | No capture reached a successful match, so the type is the pre-production swagger's. Worth doubting: every value on this broker's userinfo endpoint is a string, including two that are plainly booleans. |

Each is marked in the fidelity ledger with the provenance it actually has, so
`GET /_stubid/v1/fidelity` does not claim more than was checked.

## Transaction signing

The transaction token itself is recorded. CAP-021 and CAP-022 were taken with
`transaction_token` in scope, and CAP-022 sent a `reference_text`, which settled
`mitid.reference_text` against the `mitid.referencetext` the documentation also uses.

The transaction-*text* claims are the ones still unrecorded — `mitid.transaction_text` or
`mitid.transactiontext`, alongside `mitid.transaction_text_sha256` and
`mitid.transaction_text_type` — and the broker's own documentation contradicts itself on how
they are spelled. Nothing is emitted for them rather than guessing which spelling is real.

The sitting never asked for them: it sent `reference_text` alone. Transaction text is a
different flow, driven by the `transaction_text` and `transaction_text_type` identity-provider
parameters, and the broker's Identity Providers document limits that flow to signed requests.

A signed request works. Both clients this project can reach accept a request object signed
HS256 with the client secret, with the transaction-text parameters carried inside it — measured
with controls in [what the broker does with a signed request
object](../../research/signed-requests.md). So what stands between here and the text claims is a
sitting: no new entitlement, and nobody at the broker. StubID does not implement request objects
on its own surface, which is a separate gap, recorded in the table above.

An earlier version of this file said the text claims needed a `signtext_api` scope that only
the broker's staff could grant. That name has no source: not in the vendor documentation, not
anywhere public, and not in this repository outside the probe that used it. The capture cited
for it, CAP-016, settled a grant-type refusal rather than a scope.

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
