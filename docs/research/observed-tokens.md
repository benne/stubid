# What a real login actually returns

Recorded from Signaturgruppen Broker pre-production on 2026-08-30, during a trial run of the
capture harness. Every identifier below is replaced; only names, order and types are kept,
which is what StubID has to reproduce.

The recordings this came from were mislabelled — the operator's first pass through the
harness — so they are not committed as fixtures. The facts are still the broker's own bytes,
and they settle several questions that documentation could not.

## The id_token

Header member order: `alg`, `kid`, `typ`. `typ` is `JWT`.

Payload, in order:

| Member | Type | Note |
| --- | --- | --- |
| `iss` | string | |
| **`nbf`** | number | **Not in any vendor claim table.** |
| `iat` | number | |
| `exp` | number | 300 seconds after `iat`, matching the documented five minutes |
| `aud` | string | the client id |
| `amr` | array of string | **bare** `["code_app"]`, not `mitid.`-prefixed |
| `nonce` | string | |
| **`at_hash`** | string | **Not documented.** Present in a plain code flow |
| **`sid`** | string | **Not in any vendor claim table.** Same value as `neb_sid` |
| `sub` | string | |
| `auth_time` | **number** | |
| `idp` | string | `mitid` |
| **`acr`** | string | **Documented as belonging to the transaction token only.** It is here too |
| `neb_sid` | string | equal to `sid` |
| `loa` | string | `https://data.gov.dk/concept/core/nsis/Substantial` |
| `aal` | string | note the order: loa, **aal**, ial |
| `ial` | string | |
| `identity_type` | string | `private` |
| `transaction_id` | string | |
| **`idp_transaction_id`** | string | **Undocumented.** Differs from `transaction_id` |
| `session_expiry` | **string** | a unix timestamp as a **string**, not a number |
| **`idtoken_type`** | string | **Undocumented.** Value `strict` |
| **`subject_type`** | string | **Undocumented.** Value `org_mapped` |

`idp_environment` is **absent**, despite being documented as an id_token claim.

## The userinfo token

Returned in the token response under `userinfo_token`. Header `typ` is `at+jwt`, not `JWT`.

Order: `iss`, `nbf`, `iat`, `exp`, `amr`, `mitid.transaction_id`, `mitid.uuid`, `mitid.age`,
`mitid.date_of_birth`, `mitid.has_cpr`, `mitid.identity_name`, `loa`, `ial`, `aal`,
`identity_type`, `idp_identity_id`, `idp`, `acr`, `auth_time`, `sub`, `transaction_id`, `aud`.

Two things worth stating plainly:

- **`mitid.age` and `mitid.has_cpr` are JSON strings** (`"40"`, `"true"`). Confirmed on the
  wire rather than inferred from a documentation example.
- **`auth_time` is a string here and a number in the id_token.** The same claim, two types,
  in two tokens from the same response. Nothing would lead you to guess that.
- The assurance order differs from the id_token's: here it is loa, **ial**, aal.

## The abort

A user who cancels inside the MitID widget is redirected **back to the client**:

```
error=access_denied
error_description=mitid_user_aborted
```

Exactly as documented, and now observed. This is the distinction StubID has to reproduce: a
user-level failure comes back to the client, while an invalid *request* never does and lands
on the broker's own error page instead.

## The callback

Carries `code`, `state`, `iss` and **`session_state`** — the last of which appears in no
claim table and is not currently modelled.

## What this means for StubID

The emitted id_token was wrong in eight ways: it omitted `nbf`, `sid`, `acr`, `at_hash`,
`idp_transaction_id`, `idtoken_type` and `subject_type`; it emitted `idp_environment`, which
the broker does not send; it typed `session_expiry` as a number rather than a string; and its
member order matched no part of the real one.

None of that would have been caught by a client library. Every one of those tokens validates.
It is exactly the class of difference a stub gets wrong forever unless somebody records the
real thing.


## Single sign-on, and what the subject is really scoped to

Recorded 2026-08-31 with two clients joined to the same service provider's single sign-on.

The second client completed **without prompting**, and its id_token carried the same
`auth_time` as the first — so the session was reused rather than re-established.

The finding that matters is the subject:

| | First client | Second client |
| --- | --- | --- |
| `aud` | client A | client B |
| `sub` | *the same value* | *the same value* |
| `sid` | *the same value* | *the same value* |

**Two different clients receive the same subject.** It is scoped to the organisation, not to
the client, which is what the id_token has been saying all along in `subject_type:
"org_mapped"`. StubID derived its subject from the client id, so two clients belonging to one
company would have been given different subjects where the broker gives one — an application
that signs a user in through two of its own clients would see two different people.

Nothing would have caught this without two clients configured under one service provider. A
single client cannot show it, and no documentation states it plainly.

## c_hash

Recorded with a client on the hybrid grant, `response_type=id_token code`.

The front-channel id_token carries **`c_hash`** and no `at_hash`. The back-channel id_token
from the same flow carries **`at_hash`** and no `c_hash`. They occupy the same position:
after `nonce`, before `sid`.

ASP.NET Core requires `c_hash` whenever an id_token arrives through the front channel, so a
hybrid integration rejects a token without it. The earlier front-channel recording used
`response_type=id_token` alone, which produces neither, so nothing before this could have
shown where it sits.
