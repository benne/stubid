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
