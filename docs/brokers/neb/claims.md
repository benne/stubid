# Nets eID Broker: what the tokens carry

Everything here comes from recordings of real logins against pre-production, not from the
broker's documentation. Where the two disagree the recording wins, and they disagreed often.

Member order is part of the contract, so the tables are in the order the broker sends.

## id_token

| Member | Type | Notes |
| --- | --- | --- |
| `iss` | string | |
| `nbf` | number | Not in the broker's claim tables |
| `iat` | number | |
| `exp` | number | 300 seconds after `iat` |
| `aud` | string | the client id |
| `amr` | array | bare values, not `mitid.`-prefixed |
| `nonce` | string | when the request carried one |
| `at_hash` *or* `c_hash` | string | one slot, see below |
| `sid` | string | Not in the broker's claim tables. Same value as `neb_sid` |
| `sub` | string | scoped to the **organisation**, not the client |
| `auth_time` | number | a string in the userinfo token |
| `idp` | string | `mitid` |
| `acr` | string | documented as belonging to the transaction token alone; it is here too |
| `neb_sid` | string | |
| `loa`, `aal`, `ial` | string | in that order; the userinfo token uses loa, ial, aal |
| `identity_type` | string | `private` |
| `transaction_id` | string | |
| `idp_transaction_id` | string | undocumented, and different from `transaction_id` |
| `session_expiry` | **string** | a unix timestamp, as a string |
| `idtoken_type` | string | undocumented. `strict` |
| `subject_type` | string | undocumented. `org_mapped` |

`idp_environment` is documented as an id_token claim and is **not sent**.

### The hash slot

At most one, always after `nonce`:

- **`at_hash`** in the back-channel token, over the access token.
- **`c_hash`** in the front-channel token of a hybrid response, over the code.
- **neither** in a front-channel token from `response_type=id_token` alone, because there is
  no access token and no code to cover.

ASP.NET Core rejects a front-channel id_token whose `c_hash` is missing or wrong.

## The subject

Scoped to the organisation. Two clients belonging to one service provider receive **the same
subject** for the same person, while `mitid.uuid` is the same everywhere. The id_token names
this itself: `subject_type: org_mapped`.

This matters more than it looks. Deriving a subject per client hands an application that signs
users in through two of its own clients two different people.

## userinfo

Every value is a JSON string, the age and both flags included. `sub` comes **last**.

`session_is_active`, `session_expiry`, `idp`, `subject_type`, `idp_identity_id`, `loa`, `aal`,
`ial`, `mitid.transaction_id`, `mitid.uuid`, `mitid.age`, `mitid.date_of_birth`,
`mitid.has_cpr`, `mitid.identity_name`, *(scope-dependent members)*, `mitid.psd2`,
`mitid.geo_ip_distance_km`, *(consent members)*, `sub`.

The documented `session_status` and `session_identifier` do not exist. `mitid.psd2` and
`mitid.geo_ip_distance_km` are undocumented and always present.

Scope-dependent: `dk.cpr` (`ssn`), `nemid.pid` and `nemid.pid_status` (`nemid.pid`),
`ssn.details.status` (`ssn.details_*`), and `mitid.cpr_consent_text` /
`mitid.cpr_consent_header` after the address members.

## userinfo token

Returned in the token response when the client has the setting enabled — not because a scope
asks for it. A recording with only `openid mitid` carried one.

Its header says `at+jwt` rather than `JWT`, its assurance claims come in a different order
from the id_token's, and its `auth_time` is a **string** where the id_token sends a number.
Same broker, same response, two answers.

## The authorization response

| Flow | Parameters, in order |
| --- | --- |
| code | `code`, `state`, `session_state`, `iss` |
| hybrid | `code`, `id_token`, `state`, `session_state` |
| implicit | `id_token`, `state`, `session_state` |

**`iss` appears only when no id_token is returned.** The id_token already carries the issuer,
so the broker omits the parameter, even though discovery advertises support for it.
