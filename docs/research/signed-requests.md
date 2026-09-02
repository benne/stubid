# What the broker does with a signed request object

Measured against Signaturgruppen Broker pre-production on 2026-09-01, on two clients: the open
code client the broker publishes, and a private client belonging to a broker customer — the one
CAP-021 and CAP-022 were recorded with. Two runs, identical.

The question came out of the transaction-text claims. They were unrecorded, the broker limits
the transaction-text flow to signed requests, and nothing here had ever sent one, so it was an
open question whether a client this project can reach is able to make one at all. It is.

Every probe stops at the authorize response. No login was completed and no MitID interaction
happened: an accepted request lands on the broker's own login page, which is where the probe
stops reading.

## The answer

A request object signed **HS256 with the client secret** is accepted, on both clients.

| | | open | private |
| --- | --- | --- | --- |
| A | PAR, request object signed with the client secret | accepted, 201 | accepted, 201 |
| B | *control:* one byte of the signature flipped | `invalid_request_object` | `invalid_request_object` |
| C | *control:* signed with a random key | `invalid_request_object` | `invalid_request_object` |
| D | no `exp` claim | `invalid_request_object` | `invalid_request_object` |
| E | authorize, the object supplies every parameter | login page, 302 | login page, 302 |
| F | authorize, `idp_params` carrying `transaction_text` | login page, 302 | login page, 302 |

## What the controls establish

Without B and C, A means very little. A server that ignored the request parameter entirely and
read the query would answer exactly the same way, so "it was accepted" would be consistent with
the object never having been looked at. Both are refused, so the signature is really verified
against the client secret.

E is the other half. Its query carried `client_id`, `response_type` and `request` and nothing
else — no `scope`, no `redirect_uri`, no `nonce`, no PKCE. That query on its own is not a valid
authorization request, and it was accepted, so the broker took those parameters out of the
object.

The `ReturnUrl` on an accepted authorize is no help here: it carries the `request=` JWT verbatim
rather than the parameters the broker resolved from it.

## `exp` is required, and its absence looks exactly like a bad signature

A request object with no lifetime claims is refused. `exp` alone is enough; `iat` alone is not.

The refusal is byte-identical to the one a forged signature earns — `invalid_request_object`,
"Invalid JWT request" — so a probe that omits `exp` fails every case it tries, *including its own
negative control*, and reads as a clean "signed requests do not work here."

That is not hypothetical. It is what the first pass at this measurement concluded, on both
clients, before the missing claim was found. A ladder whose negative control cannot fail is
worth nothing, and the way that presents itself is a uniform, confident, wrong answer.

## Debug against PAR, not authorize

`GET /op/connect/authorize` answers a bad request object with a 302 to `/op/Error?errorId=…`.
The `errorId` is an opaque data-protection blob and the page says nothing useful, so every
failure looks the same.

`POST /op/connect/par` answers the same bad object with a body:

```
{"error":"invalid_request_object","error_description":"Invalid JWT request"}
```

A good one returns a `request_uri` with `expires_in: 600`. Two endpoints, the same validation,
one of them willing to say what it thinks — switching to it is what turned the first wrong
answer around.

## Why HS256 works here at all

The recorded discovery document lists HS256 in
`request_object_signing_alg_values_supported` (`fixtures/neb/pp/CAP-001/response.raw`), and the
measurement agrees with what it advertises.

Worth knowing if you meet this elsewhere: recent versions of the stack this broker appears to
run drop the symmetric algorithms from that list by default, and they have to be turned back on
deliberately — the same setting that fills in the discovery property. What this broker has
configured was not observed; only that HS256 is advertised and that it works.

## The redirect back carries `state` and goes where the object says

Added 2026-09-02, on the private client. Everything above stops at the authorize response, so
none of it says what happens on the way back — and for a signed request that matters, because
`redirect_uri` and `state` are inside the object rather than in the query. A recording harness
that matches a returning browser by `state` has nothing to match on if the broker does not
echo one, and an authorization code that arrives unattributable expires in seconds.

Asking costs no authentication. The same signed object, with `prompt=none` added and no cookie
jar behind it, has no session to satisfy, so the broker refuses — and it refuses by redirecting
to the client:

```
302 http://localhost:5099/callback?error=login_required&state=CAP-031&session_state=…#_
```

Both parameters came out of the object. So the broker resolves them on the refusal path the
same way E showed it resolves the rest on the way in, and the furniture matches what an
unsigned interaction failure already produces.

This is the interaction-failure path standing in for the success path: the same object, the
same validation, a different ending. It is the most that can be had without spending a login,
and `rehearse` now sends it for every signing step, so a sitting finds out the day before.

## What this does not settle

The claim names. F shows the transaction-text parameters are *accepted* at the authorize
endpoint. It says nothing about what the user is shown, and nothing about which of
`mitid.transaction_text`, `mitid.transactiontext`, `mitid.transaction_text_sha256` and
`mitid.transaction_text_type` reach the transaction token, or how they are spelled when they do.

That needs a completed signing login, with a human in MitID's test tool. What this measurement
changed is that such a sitting was reachable at all — no new entitlement, and nobody from the
broker. It was taken on 2026-09-02 and is recorded as CAP-031; what came back is in
[what the tokens carry](../brokers/neb/claims.md), and the half of the question this note
could not reach — what the user is shown — is in
[what the screens showed](transaction-screens.md). The broker's own page rendered the text; it
was not among the flow values MitID held for that transaction.
