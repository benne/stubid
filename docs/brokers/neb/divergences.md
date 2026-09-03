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

## A request object is read, not verified

<a id="request-objects"></a>

The broker verifies the `request` parameter's signature against the client secret, HS256, and
refuses a bad one with `invalid_request_object`. StubID reads the object and takes its
parameters without checking who signed it. What it does check is that the object can be read at
all: three segments, a payload that is base64url, that is JSON, that is an object, and that
carries `exp`. Anything else earns the same refusal the broker gives.

**Why.** The same trade twice over, and here it is forced: the signature is over the client
secret, and StubID registers no secret for a client, so there is nothing to check one against. A
test that assembles an object by hand is far more likely than an attack on a stub, and refusing
one would fail a test that works.

**What this costs.** A test asserting that a forged or wrongly-signed request object is rejected
passes against pre-production and fails here. So does one asserting that an *expired* object is
rejected: `exp` has to be there and is not compared against the clock, because checking a
lifetime while ignoring a signature is a strange half of a check to keep.

Two decisions the recordings could not settle, taken here rather than left to be discovered:

- **The object wins over the query** where both carry the same name, which is what OpenID
  Connect Core 6.1 says. CAP-031 cannot show it: its query and its object agree on the two
  names they share.
- **`iss`, `aud`, `exp`, `iat`, `nbf` and `jti` do not become request parameters.** They are the
  JWT's own furniture rather than anything a client sent, and RFC 9101 draws the same line — an
  endpoint takes the object's *authorization request parameters* out of it. Nothing downstream
  reads any of the six, so the only place the difference would show is a parked session's
  parameter view.

An empty `request=` is treated as no object at all, which is what every other optional parameter
here does with an empty value. Unmeasured: no probe sent one.

**Where the refusal came from.** CAP-046, which pushes a `request` parameter that is not a JWS
at all and records the 400 and the bytes that come back. That case also settled the status,
which until it was taken was an inference from RFC 9126 and from CAP-019's other refusal.

Three further causes earn the identical answer and stay a measurement rather than a recording:
a flipped signature byte, a random key, and a missing `exp`, each on two clients and two runs
([what the broker does with a signed request object](../../research/signed-requests.md)).
Recording one would mean committing a request object signed HS256 with the client secret, and a
compact JWS like that is a known-plaintext HMAC tag over the secret that signed it — an offline
oracle for it, in a public repository. The manual sitting reached the same conclusion from the
other direction: CAP-031 records a request object's algorithm and its segment lengths, and never
its signature.

The accepted half *is* recorded: CAP-031's authorize URL carried `client_id`, `response_type`
and `request` and nothing else, and the login it started completed.

## Advertised but not implemented

The discovery document is served from a recording, so it advertises everything the broker
does. Some of that is not implemented:

| Advertised | State |
| --- | --- |
| `backchannel_authentication_endpoint` (CIBA) | not implemented; the endpoint 404s |
| `frontchannel_logout_supported`, `backchannel_logout_supported` | ending a session works; notifying the other clients in it does not |
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

`idp_params` is read, and both texts it can carry reach the places the recordings put them. A
`reference_text` comes back whole in the transaction token and at the userinfo endpoint, the way
CAP-022 recorded. A `transaction_text` comes back as six members under both spellings, with a
digest StubID computes, and as a type and that digest — without the text — at the userinfo
endpoint, the way CAP-031 recorded. The `request` parameter CAP-031 arrived through is read on
both the authorize and the pushed path, read rather than verified, for the reason
[above](#request-objects).

**A transaction text is not gated on a signed request.** Whether the broker would take one in a
plain query is unmeasured: the claim that it takes one only inside a request object comes from
vendor prose deleted in June 2025, no probe ever sent one unsigned, and CAP-022 shows unsigned
`idp_params` being accepted in a plain query. StubID accepts a transaction text however it
arrives, because refusing one would enforce a restriction nobody has demonstrated and would fail
a test that may well work against pre-production. `mitid.transaction_signing` follows the text
rather than the transport for the same reason.

**Three things about the text are StubID's own**, because CAP-031 sent a well-formed one and is
the only recording there is:

- **A text that cannot be decoded keeps its members and loses its digest.** Four members instead
  of six in the transaction token, and at the userinfo endpoint a
  `mitid.transaction_text_type` standing without a `mitid.transaction_text_sha256` beside it.
  The alternative is an empty 500 out of the token endpoint on a value the client controls, and
  that is the one answer the broker never gives. What the broker does here is unrecorded.
- **A text with no type gets no type.** Each of the three values is conditional on its own, so a
  client that sends only a text gets four members and no invented `text`. Emitting a JSON `null`
  would break the userinfo endpoint's every-value-is-a-string invariant; emitting `"text"` would
  be StubID answering a question nobody asked it.
- **Both base64 alphabets are accepted**, and whitespace is refused rather than skipped. The
  recorded text sits in the intersection of standard and URL-safe base64 — no `+`, `/`, `-` or
  `_`, and a length that is a multiple of four — so no recording says which the broker parses.
  Whitespace is the half with a trap in it: `Convert.FromBase64String` skips it, which changes
  the answer without saying so, since a value with characters removed still decodes and decodes
  to different bytes. Refused, such a text keeps its members and loses its digest like any other
  that will not decode.

The digest itself is not a choice. It is base64 of SHA-256 over the **decoded** bytes, standard
alphabet and padded, recomputed from CAP-031 and matched. The digest over the base64 as sent —
the answer a stub reaches for first — is a different value, and there is a test that computes
both so a failure says which one was emitted.

An earlier version of this file said the text claims needed a `signtext_api` scope that only
the broker's staff could grant. That name has no source: not in the vendor documentation, not
anywhere public, and not in this repository outside the probe that used it. The capture cited
for it, CAP-016, settled a grant-type refusal rather than a scope. CAP-031 settles it from the
other side — the text claims came back on the same client and the same granted scope CAP-022
had, with nothing added to reach them.

## StubID's login page shows the transaction text and nothing else

<a id="the-login-page"></a>

The broker's authorize page is built out of the request. Its MitID widget is headed `Godkend
hos` the relying party's registered display name, and on a signing request the transaction text
stands in a panel beside the widget. StubID's login page carries the text — decoded, on
StubID's own page rather than behind a simulated authenticator, which is where the broker put it
too ([what the screens showed](../../research/transaction-screens.md)). It carries nothing else
the request sent: no client name, no `client_id`, none of MitID's furniture.

**Why the text and not the rest.** A person is being asked to approve something, and the text is
what they are approving. Everything else on the broker's page is the broker's — a widget, a
brand, a registered display name — and reproducing it would put someone else's trade dress on an
emulator, which is what
[the login page](../../guides/approvals.md#the-login-page) already gives as the reason a page
that looked convincing is a page someone can be fooled by. The client's name is a separate
matter and not a decision: StubID registers no display name for a client at all, `Client` being
a client id, its response types and an organisation.

**The text is escaped whether it says `text` or `html`.** The broker parses an html transaction
text against a tag allowlist and renders it. StubID escapes both, because this is the first
client-controlled string the page has ever shown and it is shown in a window a browser has just
been redirected to from a real authorize request. A test asserting that markup in an html
transaction text renders as markup passes against pre-production and fails here.

**What this still costs.** Driving `/op/Login` does not complete a login here in any case:
deciding a parked session renders a page instead of redirecting
([driving a browser](../../guides/browsers.md)). So a browser test can read the text off the
page, and cannot then approve and be returned to the client. That is the resume gap, which
predates this and affects every flow.

The text reaches the page from the parsed request rather than from the query the session parked
with, and that placement is the whole of it: after a push the browser arrives carrying a client
id and a request reference, and on a signed request the parameters are inside a JWS. A page fed
from the raw query works on a plain GET and goes blank on the other two arrival shapes, with no
error to say why.

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
