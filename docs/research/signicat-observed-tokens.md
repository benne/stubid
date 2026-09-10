# What a Signicat MitID login actually returns

Observed on 2026-09-10 from a self-registered sandbox account on Signicat's Digital Trust
Platform, by completing one login against the real MitID pre-production environment with a Test
Tool identity.

**Only shapes were read.** The tool that performed the exchange prints member names, JSON types,
string lengths and the order the broker sends them in, and prints no claim value — which is the
whole of what an emulator has to reproduce, and none of what belongs to the person who logged in.
Nothing was written to disk.

Two lengths are withheld here that the tool did print. `iss` and `aud` are recorded as bare
strings, because their lengths are the tenant host's and the client id's, and so are the three
name members. That is a judgment applied by hand, not a rule the tool enforces, which is worth
knowing before anyone completes the table. There are no fixtures behind this note and it is not
evidence for a fidelity claim; it is what a capture should expect to find.

A consequence worth stating plainly: where a length is consistent with a value, this note says
so and marks it unconfirmed. A length is not an observation of a value, and this project has been
wrong before by treating a plausible reading as a settled one.

## The client this came from

Claim presence is a property of the client as much as of the broker here, so the configuration is
part of the observation:

| | |
| --- | --- |
| Scopes requested | `openid profile nin idp-id` |
| `acr_values` | `idp:mitid` |
| ID Token user data | `StandardScopes` (the default; the alternatives are `All` and `Minimal`) |
| Grant | authorization code, no PKCE, no request object |

## The token endpoint response

Five members, in this order. **No `refresh_token`**, which follows from not asking for
`offline_access` rather than from anything the broker withholds.

| Member | Type |
| --- | --- |
| `id_token` | string |
| `access_token` | string |
| `expires_in` | number |
| `token_type` | string(6), consistent with `Bearer` *(unconfirmed)* |
| `scope` | string |

## The id_token header

Member order: **`kid`, `typ`, `alg`**.

That is not the same order the first broker uses, which is `alg`, `kid`, `typ`. Both are valid
JOSE and no client library cares, which is exactly why it is the sort of thing an emulator gets
wrong and never finds out. StubID's writer emits the first broker's order from a constant; a
second profile cannot share it.

## The id_token payload

Twenty members, in this order.

| | Member | Type | Note |
| --- | --- | --- | --- |
| 1 | `iss` | string | the tenant host plus `/auth/open` |
| 2 | `nbf` | number | same three-timestamp opening as the first broker, in the same order |
| 3 | `iat` | number | |
| 4 | `exp` | number | |
| 5 | `aud` | string | the client id |
| 6 | `amr` | array[1] of string(8) | consistent with `["external"]` *(unconfirmed)* — one placeholder value where the first broker enumerates six authenticators |
| 7 | `nonce` | string | |
| 8 | `at_hash` | string(22) | present in a plain code flow, as it is on the first broker |
| 9 | `sid` | string(32) | and **no second copy under another name**, where the first broker sends the same value again as `neb_sid` |
| 10 | `sub` | string(44) | matches the base64url-with-padding shape the discovery document implies by advertising `public` rather than `pairwise` |
| 11 | `auth_time` | number | |
| 12 | `idp` | string(5) | consistent with `mitid` *(unconfirmed)* |
| 13 | `name` | string | from `profile` |
| 14 | `family_name` | string | |
| 15 | `given_name` | string | |
| 16 | `birthdate` | string(10) | a date, not a timestamp |
| 17 | `idp_issuer` | string(5) | |
| 18 | `transaction_id` | string(36) | a GUID's length; the first broker sends two of these, adding `idp_transaction_id` |
| 19 | **`sandbox`** | **boolean** | the only boolean in the token, and absent from userinfo. What it does in production is unobserved |
| 20 | `acr` | string(11) | consistent with `substantial` *(unconfirmed)* — a bare eIDAS word, where the first broker sends an NSIS URI |

**`nin` is not here.** It arrives at userinfo instead, which follows from `ID Token user data`
being `StandardScopes`. Whether `All` moves it into the token is untested and is one setting away
from being known.

**No `mitid_*` claim appears at all**, because `mitid-extra` was not among the scopes requested.
That scope is where MitID's own attributes live, and none of them have been observed.

### What is absent that the first broker sends

Not a criticism of either; the point is that a shared claim composer cannot serve both. Missing
here: `loa`, `aal`, `ial` as a trio, `session_expiry` (which the first broker sends as a string
among numbers), `identity_type`, `idtoken_type`, `subject_type`, `neb_sid`, and
`idp_transaction_id`.

## userinfo

Ten members, in this order.

| | Member | Type | Note |
| --- | --- | --- | --- |
| 1 | `idp_id` | string(36) | a GUID's length. In userinfo only; the id_token carries `idp` and `idp_issuer` instead |
| 2 | `name` | string | |
| 3 | `family_name` | string | |
| 4 | `given_name` | string | |
| 5 | `birthdate` | string(10) | |
| 6 | **`nin`** | string(10) | **the CPR number, and the length a CPR has** |
| 7 | `nin_type` | string(6) | consistent with `PERSON` *(unconfirmed)*, the only value any Signicat source names |
| 8 | `nin_issuing_country` | string(2) | consistent with `DK` *(unconfirmed)* |
| 9 | `idp_issuer` | string(5) | |
| 10 | `sub` | string(44) | **last**, which is where the first broker puts it too |

Two things follow. The CPR does arrive on a free self-serve sandbox with nothing ordered, which
closes the question the documentation contradicted itself about. And userinfo is plain JSON on a
single endpoint here, where the first broker serves plain JSON at userinfo and a signed token at
a second endpoint — a difference that is a client setting on this broker rather than a fact about
it, since the response type can be set to signed or encrypted.

## What this does not settle

Every value. A length is a shape, and the six marked *unconfirmed* above are the ones a capture
should be aimed at first, because each is a claim whose value an emulator must emit exactly.

Beyond that: what `mitid-extra` adds, whether `All` moves `nin` into the id_token, what the
access token is (its length is consistent with a JWT rather than an opaque handle, which is worth
a look since the first broker's is opaque), and everything about a login that does not succeed.
