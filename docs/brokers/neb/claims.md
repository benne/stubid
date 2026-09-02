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
| `auth_time` | number | a string in the userinfo token and in the transaction token |
| `idp` | string | `mitid` |
| `acr` | string | documented as belonging to the transaction token alone; it is here too |
| `neb_sid` | string | |
| `loa`, `aal`, `ial` | string | this order and the userinfo response; the two tokens differ |
| `identity_type` | string | `private` |
| `transaction_id` | string | |
| `idp_transaction_id` | string | undocumented. Is the `mitid.transaction_id` below |
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

## transaction token

Returned when the request asked for the `transaction_token` scope, always with a
`transaction_token_ocsp_resp` beside it in the body. Signed by a **different key** from the
other three tokens of the same response: `CN=NEB Transact PP`, `kid`
`7FF447FA0FB65A7E749E8B43AC635862381F0CC3`, published in the same JWKS as the rest.

Three recordings: a login, a login that matched a CPR, and a transaction signing. Members that
only some of them carry say so.

| Member | Type | Notes |
| --- | --- | --- |
| `mitid.transaction_id` | string | the id_token's `idp_transaction_id` |
| `mitid.uuid` | string | |
| `mitid.age` | string | |
| `mitid.date_of_birth` | string | |
| `mitid.has_cpr` | string | |
| `mitid.identity_name` | string | |
| `amr` | **string** | an array in the other three tokens of the same response |
| `loa`, `ial`, `aal` | string | this order, as in the userinfo token |
| `identity_type` | string | underscored. `private` |
| `idp_identity_id` | string | |
| `idp` | string | |
| `dk.cpr`, `nemid.pid`, `nemid.pid_status` | string | scope-dependent |
| `acr` | string | |
| `ssn.details.status` | string | scope-dependent |
| `auth_time` | **string** | a number in the id_token |
| `sub` | string | |
| `transaction_id` | string | |
| `redirect_uri` | string | where `recipient_info` is documented |
| `nonce` | string | |
| `requested_scope` | string | |
| `mitid.psd2` | string | the string `"false"` |
| `mitid.reference_text` | string | reference-text flow only, and one slot earlier |
| `mitid.geo_ip_distance_km` | string | |
| *the transaction text* | string | six members, below |
| `mitid.cpr_consent_text`, `mitid.cpr_consent_header` | string | scope-dependent |
| `transaction_actions` | string *or* array | below |
| `transaction_client_ip` | string | |
| `nbf`, `iat` | number | equal |
| `exp` | number | `iat` plus 189 388 800 seconds, which is six years |
| `iss` | string | |
| `aud` | string | the client id |

`spec_ver` and `recipient_info` are documented and **not sent**. Neither is
`signing_cert_ocsp_nonce`, on a login or on a signing transaction.

### The transaction text arrives under both spellings

One recording sent one, inside a signed request object, which is the only way this broker
takes it. Each value comes back twice, prefixed and unprefixed, underscored in both:

| | | Value |
| --- | --- | --- |
| `mitid.transaction_text` | `transaction_text` | the base64 that was sent, not decoded |
| `mitid.transaction_text_type` | `transaction_text_type` | what was asked for |
| `mitid.transaction_text_sha256` | `transaction_text_sha256` | below |

`mitid.transactiontext` does not exist. Six members carry three values, so a client matching
either spelling works and one matching both sees each value twice.

Six member names in the token, and none among the flow values MitID held for the same
transaction — those carried the service provider's name, a reference text and a reference text
header, not this text. The broker rendered it on its own page instead:
[what the screens showed](../../research/transaction-screens.md).

The digest is over the **decoded** text rather than the base64 that was sent, and it is base64
of the hash — standard alphabet, padded — not hex.

The userinfo endpoint splits them. `mitid.transaction_text_type` and
`mitid.transaction_text_sha256` are there; `mitid.transaction_text` is not, so that endpoint
hands over a digest without the text it is over. A reference text comes back whole there, as
`mitid.reference_text`, with no type and no digest beside it.

### `transaction_actions` changes type

`"mitid.login"` on the login-only recording. `["mitid.login", "mitid.cpr_match"]` and
`["mitid.login", "mitid.transaction_signing"]` on the two that did something as well. A client
reading this member has to handle a string and an array.

## The authorization response

| Flow | Parameters, in order |
| --- | --- |
| code | `code`, `state`, `session_state`, `iss` |
| hybrid | `code`, `id_token`, `state`, `session_state` |
| implicit | `id_token`, `state`, `session_state` |

**`iss` appears only when no id_token is returned.** The id_token already carries the issuer,
so the broker omits the parameter, even though discovery advertises support for it.
