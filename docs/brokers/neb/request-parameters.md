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

## prompt

`login`, `none` and `select_account` are advertised in discovery. `none` is implemented as the
specification defines it: if nothing can resolve the login without asking the user something,
the client gets `login_required` back. That branch is unrecorded — reaching it against the
broker needs a client with single sign-on and a session already open.

## The parameters that are stored and nothing else

`language` and `login_hint` are accepted and kept with the request. Nothing reads them yet.
