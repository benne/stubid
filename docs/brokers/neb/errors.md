# Nets eID Broker: how it refuses things

There are four different ways this broker says no, and which one you get depends on the class
of the problem rather than on the endpoint. Getting this wrong produces an emulator that
passes every test a client library can express while sending your application somewhere it
would never have gone.

## An invalid request never reaches the client

A request the broker will not process does **not** redirect back to your `redirect_uri`. The
browser goes to the broker's own error page with an opaque reference:

```
302 Location: https://pp.netseidbroker.dk/op/Error?errorId=CfDJ8GkI9WY4...
```

Your application sees nothing at all. No callback fires, no error handler runs, and the user
is looking at a page you did not write. That is the behaviour to reproduce, because "my
callback never fired" is what the person debugging actually has to work with.

StubID emits a real protected payload for `errorId`, so it carries the same `CfDJ8` prefix and
round-trips through the same protector.

Recorded in CAP-008, CAP-009, CAP-011, CAP-040 and CAP-043.

## A user-level failure does reach the client

If the request was fine and the login itself failed, the client is told:

```
?error=access_denied&error_description=mitid_user_aborted&state=...&session_state=...
```

`error_description` carries the broker's own error code, not a sentence. A test asserting on
`mitid_user_aborted` needs that exact string. The response carries `session_state` and does
**not** carry `iss`, even though discovery advertises `authorization_response_iss_parameter_supported`.

Recorded in CAP-023.

## The token endpoint answers with a bare object

```json
{"error":"invalid_client"}
```

No `error_description`, no `error_uri`, nothing else. Which code you get:

| Situation | Answer | Recording |
| --- | --- | --- |
| No parameters at all | `invalid_request` | CAP-042 |
| Unknown client, or no secret | `invalid_client` | CAP-014 |
| Unusable or already-redeemed code | `invalid_grant` | CAP-015 |
| A grant the client may not use | `unauthorized_client` | CAP-016 |
| PAR without client authentication | `invalid_client` | CAP-019 |
| A `request` object that cannot be read (PAR) | `invalid_request_object` | measured, not recorded |

Every one carries `Cache-Control: no-store, no-cache, max-age=0`.

The last row is the exception to "bare", and it is the reason to debug a signed request against
PAR rather than authorize:

```json
{"error":"invalid_request_object","error_description":"Invalid JWT request"}
```

The authorize endpoint answers the same object with a 302 to `/op/Error?errorId=…` and says
nothing, so every failure there looks the same. A flipped signature byte, a random key and a
missing `exp` claim each earn the body above — the last of those being why a probe that omits
`exp` fails its own negative control and reads as "signed requests do not work here". Measured
on two clients and two runs rather than recorded, because no capture step sends a broken object:
[what the broker does with a signed request object](../../research/signed-requests.md).

StubID checks that the object can be read and not who signed it, so it answers this for a
malformed object and accepts a forged one. See
[divergences](divergences.md#request-objects).

StubID answers `invalid_grant` where the broker answers `invalid_client` for a *wrong* secret,
because it does not check secrets. See [divergences](divergences.md#client-secrets).

## An unauthenticated call is challenged

Two endpoints on the same host, two different challenge strings, both real:

```
/op/connect/userinfo        WWW-Authenticate: Bearer realm="IdentityServer",error="invalid_token"
/op/api/v1/mitid/matchCpr   WWW-Authenticate: Bearer
```

Note the missing space after the comma in the first. Both answer 401 with an empty body.

Recorded in CAP-017 and CAP-018.

## The endpoints that answer in their own shape

`matchCpr` is not an OAuth endpoint and does not use OAuth's error shape:

```json
{"errorMessage":"Missing Cpr parameter"}
```

and after three attempts in one session:

```json
{"errorMessage":"Cpr Match exceeded. Only 3 tries is allowed within a session."}
```

The first is recorded (CAP-021). The second is the broker's documented sentence — reaching it
needs a fourth call inside one authenticated session, which no capture has.

A successful match answers `{"cprNumberMatch":true}`, with a JSON boolean. That typing comes
from the pre-production swagger rather than a recording, and it is worth doubting: every value
this broker returns from userinfo is a string, including two that are plainly booleans.

## End session

Without a usable `id_token_hint`, `post_logout_redirect_uri` is **ignored** and the browser
goes to `/op/Account/Logout`. A client that omits the hint never comes back, and there is no
error to tell it why.

Recorded twice, in CAP-044 and CAP-045.
