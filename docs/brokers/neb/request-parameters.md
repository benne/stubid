# Nets eID Broker: request parameters

The broker's own parameters, beyond the ones OpenID Connect defines. What matters about each
is not what it means — the vendor documents that — but whether the broker *refuses* a request
over it. That answer decides whether an integration fails at the authorize endpoint or three
steps later, and it is not guessable. Every row below comes from a recording.

| Parameter | Refused at authorize? | Recording |
| --- | --- | --- |
| `client_id` unknown | yes | CAP-008 |
| `redirect_uri` unregistered | yes | CAP-028 |
| `response_type` missing | yes | CAP-011 |
| `scope` missing | **yes** | CAP-043 |
| `idp_values` naming an unknown provider | yes | CAP-009 |
| `idp_values=mitid_erhverv` | no | CAP-041 |
| `idp_params` that is not JSON | yes | CAP-040 |
| A malformed value *inside* a well-formed `idp_params` | **no** | CAP-010 |
| `simulation` with a mode the broker does not define | **no** | CAP-013 |

The three in bold are the ones that would have been implemented the other way round.

## scope

Required, and that is worth stating because nothing else says so. Discovery publishes no
`scopes_supported`, so there is no list to check a scope against and no obvious reason a
missing one would be a problem. It is: the request is refused outright.

## idp_values

A space-delimited list of identity providers. StubID accepts `mitid` and `mitid_erhverv`,
which are the two the broker names in its own error catalogue, and refuses anything else the
way CAP-009 records.

`mitid_erhverv` is accepted, but a login completed through it still produces private-identity
claims. Business identities are their own milestone.

## idp_params

A JSON object keyed by provider, URL-encoded in a GET. The encoding is checked; the contents
are not. A `uuid_hint` that is not a UUID is carried through and fails later inside the MitID
flow — which is why the broker publishes an error code for it at all, and why StubID must not
reject it up front.

StubID reads the `mitid` section and carries it with the request. Three members are acted on.
`reference_text` reaches the transaction token and the userinfo response, whole and undecoded,
exactly as CAP-022 recorded. `transaction_text` and `transaction_text_type` reach the transaction
token as six members under both spellings, with a digest StubID computes over the decoded bytes,
and reach the userinfo response as a type and a digest without the text — the way CAP-031
recorded, and the reverse of what the same endpoint does with a reference text. The transaction
text is also what the login page renders. The rest is carried and unread, which is the same thing
the broker does with a `uuid_hint` it will reject three steps later.

An unescaped `+` inside a base64 value survives both transports — a query string is not
form-encoded, and the form reader behind the push leaves one alone too. Both were measured
rather than assumed, because the recorded text contains no `+` and could not have said. A value
carrying actual whitespace is the case that loses its digest; see
[divergences](divergences.md#transaction-signing).

The `mitid` section alone. A login through `mitid_erhverv` produces private-identity claims
anyway, and what a business identity would put here is unobserved.

## simulation

Space-delimited sub-values inside one parameter:

```
simulation=no-ui uuid:<guid>
simulation=no-ui username:<name>
simulation=no-ui cpr:<10 digits>
```

The first token is the mode, `ui` or `no-ui`. The rest split on the first colon. Identity
resolution goes uuid, then username, then personal number; the vendor does not document the
order, so that part is StubID's choice.

This is the incumbent's published grammar, supported verbatim so a team already paying for the
broker's simulation add-on can point at StubID and change nothing else.

A mode the broker does not define is **ignored**, not refused — CAP-013 records the request
being accepted and sent on to the authenticator. Naming a person who does not exist is
different: that fails with `mitid_simulation_unknown_user`.

## request

An authorization request packed into a JWT, which is how the broker wants a transaction text
sent. There is no row for it in the table above, because the table's rows come from recordings
and this one's refusals come from a measurement instead —
[what the broker does with a signed request object](../../research/signed-requests.md), taken on
two clients and two runs.

The broker verifies the signature: HS256 over the client secret, which is what discovery's
`request_object_signing_alg_values_supported` advertises. It also requires `exp`, whose absence
is refused with bytes identical to a forged signature. Both refusals are `invalid_request_object`
— a body at the PAR endpoint, an opaque error page at authorize.

StubID reads the object and takes its parameters. It does not check the signature, and it checks
`exp` for presence rather than against the clock; see
[divergences](divergences.md#request-objects) for what that costs. The object's parameters win
over the query's, and its JWT claims — `iss`, `aud`, `exp`, `iat`, `nbf`, `jti` — do not become
parameters.

Both endpoints read one. A pushed request is unpacked where it is pushed, so the reference
handed back to the client already carries what the object said. The two paths part company after
that, and not because of the object: a redeemed `request_uri` arrives at the authorize endpoint
with a query holding the client id and the reference, so what decides a parked login sees the
query rather than the pushed parameters. That gap predates this and is the same one
[the login page](divergences.md#the-login-page) entry describes.

## prompt

`login`, `none` and `select_account` are advertised in discovery. `none` is implemented as the
specification defines it: if nothing can resolve the login without asking the user something,
the client gets `login_required` back. That branch is unrecorded — reaching it against the
broker needs a client with single sign-on and a session already open.

## The parameters that are stored and nothing else

`language` and `login_hint` are accepted and kept with the request. Nothing reads them yet.

`simulation` is read. So are three members of the `mitid` section of `idp_params`:
`reference_text`, `transaction_text` and `transaction_text_type`.
