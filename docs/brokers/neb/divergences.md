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

StubID issues no transaction token. Its token response carries `id_token`, `access_token`,
`expires_in`, `token_type`, `scope` and `userinfo_token` and stops there: no
`transaction_token`, and no `transaction_token_ocsp_resp` beside it. `idp_params` is checked
for being a JSON object and its contents are never read, so `transaction_text` and
`reference_text` are accepted and discarded.

**Why.** Not because anything is unknown. What the broker sends is recorded three times over —
CAP-021, CAP-022 and CAP-031 — and written up in [what the tokens carry](claims.md). Until the
feature is built, issuing part of a transaction token would be worse than issuing none: a
client that gets one and finds a member missing has been told something false about a token
whose whole purpose is to be evidence.

**What this costs.** A test that drives a signing flow has nothing to assert against. There is
no partial credit on offer here.

The `request` parameter is the other half of the same gap. The broker limits the
transaction-text flow to signed requests, and StubID does not implement request objects, so an
application exercising that flow against the stub cannot send what the real broker requires.
That one is in the table above; this one is its own section because nothing advertises it.

An earlier version of this file said the text claims needed a `signtext_api` scope that only
the broker's staff could grant. That name has no source: not in the vendor documentation, not
anywhere public, and not in this repository outside the probe that used it. The capture cited
for it, CAP-016, settled a grant-type refusal rather than a scope. CAP-031 settles it from the
other side — the text claims came back on the same client and the same granted scope CAP-022
had, with nothing added to reach them.

## StubID's login page shows nothing the request carried

The broker's authorize page is built out of the request. Its MitID widget is headed `Godkend
hos` the relying party's registered display name, and on a signing request the transaction
text stands in a panel beside the widget. StubID's login page shows none of that: it says it
is an emulator, offers a dropdown of citizens and an Approve and an Abort button, and names
neither the client nor anything the request carried.

**Why.** Not wearing MitID's furniture is the deliberate half, for the reason
[the login page](../../guides/approvals.md#the-login-page) already gives — a page that looked
convincing is a page someone can be fooled by. The client's name is the other half, and not a
decision: StubID registers no display name for a client at all, `Client` being a client
id, its response types and an organisation, and the page does not show even the `client_id` it
has. That one is an omission rather than a position.

**What this costs.** A browser test that reads the transaction text off the page before
approving passes against pre-production and fails here. The cost falls on a person watching
the page rather than on a test suite, because driving `/op/Login` does not complete a login
here in any case: deciding a parked session renders a page instead of redirecting
([driving a browser](../../guides/browsers.md)).

Building transaction signing does not on its own remove this. `idp_params` reaches the
decision ladder as the raw string it arrived as, and the one function that would decode it,
`RequestGrammar.IdentityProviderParameters`, has no callers anywhere. `AuthSession` keeps the
raw query rather than the parsed parameters, and that query carries the parameter only on a
GET — on a POST it is empty, and on the pushed-request path it holds the client id and the
request reference. Whoever closes those gaps should put the text on StubID's own page rather
than behind a simulated authenticator, because that is where the broker puts it:
[what the screens showed](../../research/transaction-screens.md).

## The OCES3 certificate chain

The broker signs its transaction token with a certificate issued by a Danish state CA and
returns an OCSP response alongside it. StubID issues neither, per the section above, so this
says what the divergence will be rather than what it is today. It is written down now because
building the feature is not what removes the constraint.

A client that resolves a signing key by `kid` from the published key set — which is what the
broker's own verification guide tells you to do — works against both, and CAP-031 is the first
capture where that path ran end to end against the broker: every token verified under the key
its `kid` resolved to in the key set as it stood that day. A client that validates the
certificate *chain*, or the OCSP response, works against pre-production and fails here. There
is no fix for that; it is disclosed rather than papered over.

What StubID would have to produce is no longer a guess. The three recorded responses are
decoded and asserted by `OcspResponseContractTests`, and they agree on all of it: a successful
basic response holding exactly one answer, `good`, whose CertID names the transaction-signing
certificate by the SHA-1 of its issuer's name and its serial number; signed ECDSA-with-SHA-256
by a delegated responder — enhanced key usage OCSP signing and nothing else, carrying
`id-pkix-ocsp-nocheck` — whose own certificate travels inside the response and was issued by
the same state CA; no nonce and no response-level extensions at all; and one non-critical
archive-cutoff extension on the answer. `producedAt` is minutes *before* the response that
carries it, because the broker serves an answer it already had rather than asking for a fresh
one, so a stub minting `producedAt` at the moment of the token would diverge on the one field
that is easiest to get wrong by being helpful.

## What is not reproduced

Infrastructure that belongs to the broker's hosting rather than its protocol: the `server`
header, `x-neb-site`, HSTS and CSP headers, the wording of the error page, and response
timing. StubID emits an `X-StubID-Emulator` header of its own so an instance cannot be
mistaken for the real thing.
