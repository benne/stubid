# Day-zero probes

Two questions had to be answered before writing any code, because both could have changed
the shape of the project. Both were answered on 2026-08-28 with unauthenticated GET
requests against the Signaturgruppen Broker pre-production environment, using the
open test clients the broker publishes.

## 1. Can the capture harness use a localhost redirect URI?

**Yes.** This matters because the harness records a real authorization code flow, and it
runs on a developer's machine.

The broker documents the open test clients as accepting arbitrary redirect URIs. Confirmed:

| `redirect_uri` | Result |
| --- | --- |
| `http://localhost:5099/callback` | 302 to `/op/Account/Login` |
| `http://127.0.0.1:5099/cb` | 302 to `/op/Account/Login` |
| `https://example.invalid/cb` | 302 to `/op/Account/Login` |
| *(control)* unknown `client_id` | 302 to `/op/Error?errorId=…` |
| *(control)* `idp_values=nosuchidp` | 302 to `/op/Error?errorId=…` |

The controls matter: they show the endpoint does reject invalid requests with the error
page, so the accepted cases are genuinely accepted rather than uniformly waved through.

Two details worth keeping for the emulator: a valid authorize request redirects to
`/op/Account/Login?ReturnUrl=%2Fop%2Fconnect%2Fauthorize%2Fcallback%3F…` — note the
internal `/op/connect/authorize/callback` path and the re-encoded original query — and an
invalid one never redirects back to the client.

## 2. Is `simulation=no-ui` usable on the open test clients?

**No.** The parameter is ignored, so it cannot be used to automate our recordings.

The broker sells a `simulation=ui|no-ui` authorize parameter that completes a login without
user interaction. If the open clients were entitled to it, the whole manual recording round
could have been automated.

Every variant below reached the login page unchanged:

| `simulation` value | Result |
| --- | --- |
| `no-ui uuid:<guid>` | login page |
| `ui uuid:<guid>` | login page |
| `no-ui username:stubid-probe` | login page |
| `no-ui cpr:<replacement cpr>` | login page |
| `no-ui` (no identity given) | login page |
| **`totally-invalid-mode`** | **login page** |

The last row is the one that settles it. The broker publishes error codes
`mitid_simulation_error_parameter` and `mitid_simulation_unknown_user`, so a client that
had simulation enabled would fail on a malformed value. Reaching the login page instead
means the parameter is not being parsed for this client at all. That is consistent with
simulation being a per-client entitlement sold separately.

### Confirmed a second way

The first probe used the broker's published open clients, which left one reading open: the
parameter might work on a client that had been entitled to it. It has since been checked
against a private pre-production client belonging to a broker customer, with the same result,
and the customer's own broker administration offers no setting to turn it on.

So simulation is not something an integrator can obtain by configuration. It is sold, and it
is enabled on the broker's side.

### Consequence

The manual recording pass stays in the plan and stays expensive: one human, a provisioned
test identity, and a couple of hours, to settle the facts that only a completed login
reveals — the `amr` wire form, the id_token member set and order, the userinfo value types,
and the transaction token's claim names.

## 3. What a broker customer can and cannot configure

Checked against a real pre-production broker administration, which bounds what any recording
session can reach without involving the broker's own staff.

| | |
| --- | --- |
| Registered redirect URIs | configurable |
| Back-channel logout URI | configurable |
| `simulation` | **no setting exists** |
| Which scopes a client may request | **no control exists in the interface** |

The last row bounds what any sitting can ask for: the administration offers no way to add a
scope to a client, so a recording can only request what the client already carries. Everything
else the sitting wants is reachable.

The `simulation` row confirms the product thesis from the other direction. The capability
exists, it is worth paying for, and it is not available to someone who just wants to run their
test suite.

### Correction, 2026-09-01

The last row originally named a scope, `signtext_api`, and recorded that it could not be
granted from the administration interface. The paragraph under it concluded that the
transaction token's text claims needed a scope only the broker's staff could grant.

That name has no source. It appears in no vendor document and nowhere public, and its first
occurrence in this repository is the probe request that used it. That probe, CAP-016, was
refused `unauthorized_client` — the error for a client not authorized to use a **grant type**,
where an unknown or unentitled scope is `invalid_scope`. It was a code client asking for
`client_credentials`, so the request failed on the grant and the scope string was never read.
The fixture settles the error shape, which is what it was recorded for, and nothing about the
scope.

What the administration was observed to lack is any scope-granting control, which is the row as
it now reads. The real reason the 2026-08-30 sitting did not record the text claims is that it
sent `reference_text` alone, and the broker limits the transaction-text flow to signed requests
— see [the divergences](../brokers/neb/divergences.md).

**Recorded 2026-09-02.** CAP-031 sent a signed request carrying a transaction text and the
claims came back, on the same client and the same granted scope as the sitting before it. No
scope was added to reach them, which is the retraction's positive half: the argument above
rests on a missing source and on reading one error shape, and this rests on the claims arriving.
