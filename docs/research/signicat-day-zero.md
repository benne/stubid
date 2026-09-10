# Signicat day-zero: what the dashboard settles, and what it does not

Four questions had to be answered before any Signicat recording was worth planning, because
each could have changed the shape of the work. They were answered on 2026-09-10, partly from a
self-registered sandbox account on Signicat's Digital Trust Platform — whose eID product is the
"eID and Wallet Hub" — and partly from the platform's own discovery document.

Two kinds of evidence are mixed here and are kept apart on purpose. **The dashboard** says what
is *possible*: which scopes a client may hold, which toggles exist. **The discovery document**
is the platform speaking for itself and is quoted as fetched. Neither is a recording of a
login. One question needed one to close, and section 3 says so where it relies on it; everything
else below stops at what is possible. No claim name, JSON type or member order is established
here — that is [a separate note](signicat-observed-tokens.md), and neither is a recording.

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

**A scope, and the flow it drives runs.** A login on a client holding `nin` asks for the CPR
number as its second step, on a free sandbox, with nothing ordered and nobody contacted.

A completed login settles the rest of it: `nin` arrives, at userinfo, ten characters long, which
is the length a CPR has. What a claim is *called* and where it appears is in
[the observed tokens](signicat-observed-tokens.md); what matters here is that nothing was ordered
and nobody was contacted to make it appear.

What is established: `nin` is one of the scopes a client can hold, and the dashboard states it
declares three claims — `nin`, `nin_type` and `nin_issuing_country`. There is no CPR-matching
toggle anywhere in the dashboard, no CPR field on a client, and no CPR page in the
configuration documentation.

The documentation could not have told you this. Signicat's own attributes reference presents the
CPR-match response under MitID *without* add-ons, and its migration guide says only to make sure
`nin` is among the requested scopes — both of which read as "just ask for it". Against that stands
one sentence, in a migration table, saying CPR matching must be enabled, naming no toggle, no
ticket and no contact. The two cannot be reconciled from outside, and the wire settles it.

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

**Nothing**, beyond a key the harness has to grow anyway. It is already live on a free
self-serve sandbox, and that was established by using it rather than by asking.

The documentation says close to the opposite: the add-on is ordered through Signicat and a flag is
then set on the account's MitID configuration, a surface no published page or field list shows. So
there is nothing to find in the dashboard, which is the symptom that sends somebody looking for a
support address.

Two signed requests settle it, identical but for one carrying `mitid_reference_text` inside its
`acr_values`. The MitID test app's simulator shows a `Flow Value Texts` panel, and the difference
between the two is the whole result:

| | Control | Carrying a reference text |
| --- | --- | --- |
| `Reference Text` | *(empty)* | the exact string that was sent |
| `Reference Text Header` | `Log on at Signicat demo` | `Log on at Signicat demo` |
| `Service Provider` | `Signicat demo` | `Signicat demo` |

The control is what makes that an answer rather than an impression. A populated field on its own
says nothing about what populated it, and an empty one says nothing about entitlement.

Two of those three rows were never set by either request. `Reference Text Header` and
`Service Provider` are Signicat's defaults for an account that has not asked for its own, so the
citizen is told they are logging on at "Signicat demo" whoever the client belongs to. Both are
documented as things Signicat configures on request, and both are text a person reads before
approving. A recording made before they are set carries the default; the account's own name
appears the day they are.

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

Registering one is two different acts wearing similar labels. **Import public key** takes a key
you already hold, which is the one a capture harness wants: the private half never leaves the
machine that signs with it. **Add public key** has Signicat generate the pair and show you the
private half once, never storing it — convenient, and the wrong shape for a key that has to be
reproducible from a checked-out repository. Either way the key carries a name, a validity window
defaulting to about three months, and a usage of either signing or encryption. Signing is the one
that verifies a request object; encryption is for receiving encrypted responses, which is a
different mechanism on the same page.

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

## 8. What an unauthenticated authorize request settles

Sent the way the first broker's day-zero probes were sent: real requests to the real endpoint,
no login completed, nothing recorded as a fixture.

**`acr_values` is read, and the negative control proves it.** The account has one eID, so a
request naming nothing still reaches MitID and on its own says nothing. A request naming an eID
that does not exist is what separates the two readings:

| `acr_values` | Where it lands |
| --- | --- |
| *(absent)* | MitID |
| `idp:mitid` | MitID |
| **`idp:nosuchidp`** | **a "Select identity provider" chooser** |

**But an unknown key is ignored rather than refused.** `idp:` is validated; the keys beside it
are not. A request carrying `nosuchkey:abc` reaches MitID exactly as the baseline does, and so
does one carrying `mitid_reference_text`, in every spelling tried — space-separated,
comma-separated, and alone. So the absence of a complaint about the reference text establishes
nothing about whether the account may use it. This is the same trap the first broker set with
`simulation`, where a deliberately invalid value reaching the login page was the row that
settled it.

Nor is there an error to look for. The platform's published error catalog is thirty-three
entries under five prefixes, and not one of them mentions consent, a reference text, an add-on
or an entitlement.

**The MitID journey leaves the tenant host entirely.** The redirect chain ends on the older
Enterprise infrastructure, at `preprod.signicat.com/std/method/dtp`, whose target parameter names
a `saml11target` — SAML 1.1, internally, behind an OIDC front door. The page it serves is a small
shim that hands off to the widget at `signicat.pp.mitid.dk`.

That last hostname settles something the documentation only implied: the sandbox is backed by the
real MitID pre-production environment, on a Signicat-branded subdomain of it. It also bounds what
an emulator is responsible for. Everything from the shim onward is MitID's own surface on
somebody else's host, which StubID does not reproduce and does not need to.

**Two error-surface facts, both worth recording.** An unusable request object redirects to
`/auth/open/Error?errorId=CfDJ8…` — the ASP.NET Core Data Protection payload, the same shape the
first broker uses. The page renders `An error has occured`, misspelled, which is a functional
fact this project reproduces exactly rather than corrects. Below it sits `Invalid JWT request`,
which is character-for-character what the first broker returns as the `error_description` from
its pushed-authorization endpoint. Two vendors, one IdentityServer underneath.

And the reason is not distinguishable from outside: a correctly signed request object whose key
is not registered, and a `request` parameter that is outright garbage, produce the same page and
the same words. An emulator can reproduce that; a client debugging against it cannot tell the two
apart, which is the broker's choice rather than ours.

## Two spellings that are both correct

`mitid-private-business` is a scope. `mitid-private-to-business` is an eID scoping code, used as
a value of `idp:` inside `acr_values`. They name the same product in two vocabularies, and an
emulator that treats either as a typo for the other will refuse a request the broker accepts.

## What this does not settle

Everything a login produces, as bytes. One login was completed to close two questions, and what
it returned is written up beside this as shapes rather than values — but no Signicat response has
been recorded, no MitID-specific claim has been seen at all, and the dashboard's own warning about
scopes is a fair statement of what its claim lists are worth until one is.

Two of the four questions this note opened with are now answered by observation rather than by
reading, and both answers contradict what the documentation implies. What is left is narrower.

Ranked, the questions a first sitting still has to answer: what a canceled and a timed-out login
return, for which no code is quotable today and no entitlement error exists in the published
catalog; whether the path below `/auth/open` is matched strictly or loosely, which decides what
the profile's tenant root declares; and every claim name, JSON type and member order, none of
which any amount of dashboard reading can supply.

One methodological note, because it is the reason two of these closed. Both answers came from a
pair of requests differing in one parameter, run against the real environment, with the control
run first. Neither could have come from a single request, and neither is visible anywhere but
inside the MitID app.
