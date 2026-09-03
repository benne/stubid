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

<a id="transaction-signing"></a>

StubID issues a transaction token when the request asks for the `transaction_token` scope, with
a `transaction_token_ocsp_resp` beside it. The pair is never split, because no recorded body
splits it.

`idp_params` is read as far as `reference_text`, which reaches the transaction token and the
userinfo response the way CAP-022 recorded. What is not there yet is the *transaction* text: the
six members carrying it under both spellings are absent, and the `request` parameter they were
recorded arriving through is unimplemented. That one is in the table above.

Whether the broker would take a transaction text *without* a signed request is unmeasured. The
claim that it takes one only inside a request object comes from vendor prose deleted in June
2025, and no probe ever sent one unsigned — while CAP-022 shows unsigned `idp_params` being
accepted in a plain query. So the order here is what the recordings could reach, not a
constraint anyone demonstrated.

**Why the token came first.** Because the recordings put it there. The transaction token is
gated on the scope rather than on a signed request: CAP-021 and CAP-022 are plain unsigned
authorize URLs with no `request` parameter, and both came back with one. Only the transaction
text needs a signed request, so the token could be built and checked against two recordings
before any of the request-object work existed.

**What this costs.** A test that drives a signing flow gets a well-formed transaction token
whose transaction-text members are absent — so a client reading `transaction_text` finds nothing,
where against pre-production it would find the text it sent. That is a smaller gap than issuing
no token at all, and unlike the members themselves it is visible: the claim is missing rather
than wrong. A `reference_text` flow is complete.

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

Building transaction signing does not on its own remove this. `idp_params` is decoded now, but
it is decoded into the request rather than into the session: `AuthSession` keeps the raw query,
and that query carries the parameter only on a GET — on a POST it is empty, and on the
pushed-request path it holds the client id and the request reference. So the page has nothing to
render even though the token does. Whoever closes that should put the text on StubID's own page
rather than behind a simulated authenticator, because that is where the broker puts it:
[what the screens showed](../../research/transaction-screens.md).

## The OCES3 certificate chain

<a id="the-oces3-certificate-chain"></a>

The broker signs its transaction token with a certificate issued by a Danish state CA and
returns an OCSP response alongside it. StubID now returns one too, and this is what is
different about it.

The shape is reproduced, and it was not guessed. The three recorded responses are decoded and
asserted by `OcspResponseContractTests`, they agree on all of it, and StubID writes the same
structure: a successful basic response holding exactly one answer, `good`, whose CertID names
the transaction-signing certificate by the SHA-1 of its issuer's name and its serial number;
signed ECDSA-with-SHA-256 by a responder whose own certificate travels inside the response,
with enhanced key usage OCSP signing and nothing else and carrying `id-pkix-ocsp-nocheck`; no
nonce and no response-level extensions at all; and one non-critical archive-cutoff extension on
the answer.

Four things differ. The first three cannot be closed by writing more code; the fourth is a
choice:

**The responder is self-signed.** The broker's is a *delegated* responder issued by the same
state CA as the certificate it answers about. StubID has no CA — every certificate it makes is
self-signed with `CA=false`, and .NET refuses such a certificate as an issuer — so two relations
that hold on every recording do not hold here: the responder was not issued by the
certificate's own CA, and the CertID's `issuerKeyHash` is not an Authority Key Identifier,
because a self-signed certificate made here carries none. `OcspWriterTests` asserts both
absences, so nobody reads the recordings and expects them.

**`producedAt` is now.** The broker serves an answer it already had — CAP-031's is three and a
half minutes before the response carrying it — where StubID mints one per response. Caching to
reproduce the staleness would make the output depend on how long the instance had been running,
which is the opposite of what a test wants.

**The archive cutoff is StubID's own beginning.** The recorded cutoff is a fixed 2021 date, the
same on recordings taken days apart, because it says how far back the CA keeps answers. StubID
uses the moment its own signing certificate began, since nothing before that can be asked about.

**`nextUpdate` is a day out, not five years.** This one is reproducible and deliberately is not.
The recorded responder sets `nextUpdate` one second before its own certificate expires, so with
StubID's five-year responder the answer would claim to be good until 2031 and a client that
caches it would never ask again. A day is the conventional interval and keeps the exchange
observable. One line — `responder.NotAfter.AddSeconds(-1)` — would match the recording exactly
if a test ever needs it to.

A client that resolves a signing key by `kid` from the published key set — which is what the
broker's own verification guide tells you to do — works against both, and CAP-031 is the first
capture where that path ran end to end against the broker: every token verified under the key
its `kid` resolved to in the key set as it stood that day. A client that validates the
certificate *chain*, or that checks the OCSP response against a trusted issuer, works against
pre-production and fails here. There is no fix for that; it is disclosed rather than papered
over.

## What is not reproduced

<a id="emulator-header"></a>

Infrastructure that belongs to the broker's hosting rather than its protocol: the `server`
header, `x-neb-site`, HSTS and CSP headers, the wording of the error page, and response
timing. StubID emits an `X-StubID-Emulator` header of its own so an instance cannot be
mistaken for the real thing.
