# Signicat day-zero: what the dashboard settles, and what it does not

Four questions had to be answered before any Signicat recording was worth planning, because
each could have changed the shape of the work. They were answered on 2026-09-10, partly from a
self-registered sandbox account on Signicat's Digital Trust Platform — whose eID product is the
"eID and Wallet Hub" — and partly from the platform's own discovery document.

Two kinds of evidence are mixed here and are kept apart on purpose. **The dashboard** says what
is *possible*: which scopes a client may hold, which toggles exist. **The discovery document**
is the platform speaking for itself and is quoted as fetched. Neither is a recording of a
login, so nothing below establishes a single claim name, JSON type or member order. That still
has to be recorded.

This is the current generation throughout. The older Enterprise platform serves `/oidc` and
names its MitID claims with full stops; it is a different product and mixing the two is the
fastest way to build something wrong.

The account's own subdomain and client identifier are written as `<subdomain>` and
`<client-id>`. Signicat's tenancy is in the hostname, so unlike the first broker, the account
name is part of every issuer and every absolute URL in the metadata.

## 1. Which authority is a MitID client issued?

**The tenant host, under `/auth/open`.** Not a `mitid.dk` host.

This was the question everything else waited on. Signicat's documentation names
`signicat-id.mitid.dk` as the MitID subdomain in its basic profile, and that host serves a
complete OIDC discovery document of its own at `/oidc` — the older generation's prefix. Two
live surfaces, either of which a new client could plausibly have been issued on, and emulating
the wrong one produces a stub nobody can point an application at.

A newly created OIDC client's overview names both, and neither is a `mitid.dk` host:

| | |
| --- | --- |
| Issuer URL for client | `https://<subdomain>.sandbox.signicat.com/auth/open` |
| Well-Known URL for client | `https://<subdomain>.sandbox.signicat.com/auth/open/.well-known/openid-configuration` |

So the issuer is path-bearing, as Nets eID Broker's is, but the tenant is the hostname rather
than the path segment. Both halves matter: the path is what a profile's tenant root declares,
and the hostname is what the public base URL has to be able to carry.

## 2. Does a localhost redirect URI over plain HTTP work?

**Yes**, and it is a documented rule rather than a lenient validator.

`http://localhost:5099/callback` was accepted and saved. Had it been refused, the capture
harness would have needed TLS on its own relying party before a sitting could happen at all,
since it serves plain HTTP on port 5099.

Worth recording *why* the answer looked uncertain, because the same trap is waiting elsewhere.
The dashboard's own prose says a redirect URI must use HTTPS, and its only localhost example is
`https://localhost:5001`. The complete rule is stated in one place, the configuration API's
schema: an absolute HTTPS URI, with HTTP allowed when it points at localhost. Two Signicat
sources, one incomplete, and the incomplete one is the one a reader meets first.

`127.0.0.1` is named by neither, and was not tried.

## 3. What gates the CPR number?

**Unsettled, and only a login will settle it.** The scope exists and is selectable; whether the
number actually arrives is a different question, and the documentation contradicts itself on it.

What is established: `nin` is one of the scopes a client can hold, and the dashboard states it
declares three claims — `nin`, `nin_type` and `nin_issuing_country`. There is no CPR-matching
toggle anywhere in the dashboard, no CPR field on a client, and no CPR page in the
configuration documentation.

What is not established: whether holding the scope is sufficient. Signicat's own attributes
reference presents the CPR-match response under MitID *without* add-ons, and its migration
guide says only to make sure `nin` is among the requested scopes — both of which read as "just
ask for it". Against that stands one sentence, in a migration table, saying CPR matching must be
enabled, naming no toggle, no ticket and no contact. The two cannot be reconciled from outside.

The scope picker is not the evidence it appears to be either. The platform's discovery document
advertises fifty-nine scopes and the list is identical on unrelated hosts, so it is a catalog
of what the platform knows rather than a statement about one account's entitlements.

The dashboard is candid about the same gap in its own way, and the sentence is worth keeping
because it is the argument for recording rather than reading:

> These are the claims included in the scopes you have selected. Please be aware that it is not
> guaranteed that all claims will be included in all authentications.

Observed with `openid`, `profile`, `nin` and `idp-id` selected:

| Scope | Claims it declares |
| --- | --- |
| `openid` | `sub` |
| `profile` | `name`, `family_name`, `given_name`, `middle_name`, `nickname`, `preferred_username`, `profile`, `picture`, `website`, `gender`, `birthdate`, `zoneinfo`, `locale`, `updated_at` |
| `nin` | `nin`, `nin_type`, `nin_issuing_country` |
| `idp-id` | `idp_id` |

## 4. What will transaction consent cost?

**More than a checkbox, and the harness needs a new signer either way.**

There is no transaction-consent setting in the dashboard, and unlike the CPR question this one
has a documented answer: the add-on is ordered through Signicat, and a flag is then set on the
account's MitID configuration — a surface no published page, screenshot or field list shows. So
it is not something to find. It is something to ask for, and whether Signicat grants it on a
free sandbox is not documented either way.

The mechanics on this side are self-serve, and they are what the harness has to be ready for.
`mitid_reference_text` is a request-time `acr_values` key carrying Base64 text, and it must
travel inside a signed request object. The client has a **Requires Request Object** toggle, and
a **Public keys** page for registering the key that verifies it. That page names MitID
explicitly as a reason a customer needs one:

> Some implementations require an extra level of security, either because of requirements in
> your own company or requirements mandated by certain eIDs (eg, FTN or MitID).

The platform settles the algorithm question without ambiguity. Its discovery document advertises
`request_object_signing_alg_values_supported` as `RS256 RS384 RS512 PS256 PS384 PS512 ES256
ES384 ES512` — nine entries, every one asymmetric, and no `HS*` among them.

The capture harness signs its request objects HS256 with the client secret, because that is what
the first broker accepted. That will not work here. A key pair, an asymmetric signer, and a
public key registered on the client are preparation for a sitting rather than something to
discover during one.

## 5. Two things the first broker has no equivalent of

Both were found while looking for something else, and both change what a profile has to express.

**Token lifetimes are per-client and editable.** Nets eID Broker's are fixed facts recorded from
its responses. Signicat's are fields on a page, and their defaults differ enough to matter:

| | Signicat default | Nets eID Broker, recorded |
| --- | --- | --- |
| Identity token | 600 | 300 |
| Access token | 600 | 10800 |
| Authorization code | 15 | not recorded |
| User SSO | 3600 | 16200 (session) |
| Refresh token, absolute and sliding | 86400 | — |

The authorization code's fifteen seconds is the one to plan around: a sitting that pauses
between the callback and the token request loses the code.

**A claim can be aliased per client.** The Custom claims page creates "an alias (copy) of an
existing claim", returned in the same scope alongside the original, so that a migrated customer
can keep the names their integration already expects.

A claim name on this broker is therefore a property of the client as well as of the broker,
which is not true of the first one. Anything recorded has to say which client configuration
produced it.

## 6. The discovery document, as fetched

Taken from a public host on the same platform rather than from the sandbox account, so it is
evidence about the platform and not about one tenant. Two fields are known to be tenant-specific
and are excluded here: `claims_supported`, which carries an account's custom claims, and the key
set behind `jwks_uri`.

Forty-two members. The endpoint set is complete for an IdentityServer deployment — authorize,
token, userinfo, endsession, par, ciba, introspect, revocation, deviceauthorization,
checksession — all under `/auth/open/connect/`, with the key set at the unusual
`/auth/open/.well-known/openid-configuration/jwks`, which is the same layout the first broker
serves and the same product underneath.

| Member | Value |
| --- | --- |
| `response_types_supported` | `code`, `code id_token` |
| `id_token_signing_alg_values_supported` | `RS256` |
| `code_challenge_methods_supported` | `S256` |
| `subject_types_supported` | `public` |
| `token_endpoint_auth_methods_supported` | `client_secret_basic`, `client_secret_post`, `private_key_jwt`, `tls_client_auth`, `self_signed_tls_client_auth` |
| `claims_parameter_supported` | `false` |
| `request_parameter_supported` | `true` |
| `authorization_response_iss_parameter_supported` | `true` |

Two absences are worth as much as the values. There is no `registration_endpoint`, so dynamic
registration is not offered. And there is no `acr_values_supported`, even though `acr_values` is
how MitID is selected and how its options are carried — a client cannot discover the one
parameter it most needs. The first broker omits that key too, along with `scopes_supported` and
`claims_supported`; this one omits only that.

`subject_types_supported` being `public` rather than `pairwise` is the item to watch when the
recordings start. The first broker's subject is scoped to the organization, and a subject that
is stable across clients is a different fact for a test suite to rely on.

## 7. The client surface, for the record

A second broker's request grammar starts here.

**Grant types**, one chosen as primary: `AuthorizationCode`, `Hybrid`, `Ciba`.

**Templates** offered at creation: no template, FTN, Hardened Security, MobileID Authorization
Code Flow, MobileID Ciba, Native/Mobile Application, Single-Page Application.

**Security toggles**: requires secret (on by default), requires PKCE, requires consent, requires
request object, requires pushed authorization requests, force login (disable SSO), use reference
access tokens, allow access tokens via browser, require encrypted ID tokens.

Note that requires-consent is ordinary OAuth scope consent and has nothing to do with MitID
transaction consent. It is the first thing a search for "consent" lands on.

**Three-way choices**: ID token user data (`All`, `Minimal`, `StandardScopes`); userinfo response
type (`Json`, `Signed`, `Encrypted`, `SignedAndEncrypted`); content encryption algorithm
(`A128CBC-HS256`, `A192CBC-HS384`, `A256CBC-HS512`).

The default of `StandardScopes` matters for a first recording: MitID's own claims are not in the
standard set, so a fresh client shows none of them in the id_token regardless of entitlement.
They arrive at userinfo, or in the id_token once the setting is `All`.

The userinfo response type is worth noting beside the first broker, which serves plain JSON at
its userinfo endpoint and a signed token at a separate one. Here it is one endpoint and a
setting.

**Identity provider restrictions** is a per-client allowlist whose dropdown offered exactly the
eIDs the account has added — `mitid` and nothing else. Beside it sit a free-text **ACR values**
field and a **Force use ACR values** checkbox, which is where a client-level default for
`idp:mitid` would go.

## Two spellings that are both correct

`mitid-private-business` is a scope. `mitid-private-to-business` is an eID scoping code, used as
a value of `idp:` inside `acr_values`. They name the same product in two vocabularies, and an
emulator that treats either as a typo for the other will refuse a request the broker accepts.

## What this does not settle

Everything a login produces. No byte of a Signicat response has been recorded, no MitID claim
name has been observed, and the dashboard's own warning about scopes is a fair statement of how
much its claim lists are worth until one has been.

Ranked, the questions a first sitting has to answer: whether `nin` actually arrives with the
scope alone; whether transaction consent can be had on a free sandbox at all; what a canceled
and a timed-out login return, for which no code is quotable today; and whether the path below
`/auth/open` is matched strictly or loosely, which decides what the profile's tenant root
declares.
