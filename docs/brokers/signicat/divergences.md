# Signicat: where StubID differs on purpose

Every entry here is a deliberate decision, not an omission. An instance serving this broker answers
the same list at `GET /_stubid/v1/fidelity`, read from annotations next to the code that emits each
behavior, so this document and the running system cannot disagree.

This broker is declared rather than emulated: two endpoints are served and the rest say what they
are. [The whole ledger](#the-whole-ledger) is at the end, and [what a recording would
settle](#awaiting-capture) lists anything the ledger is still waiting on.

## A login has not been recorded

<a id="no-login-yet"></a>

Authorize, token, userinfo, end session, check session and pushed authorization all answer 501.

**Why.** What none of them records is a login that succeeded, because MitID's test tool needs a
person at a keyboard and that sitting has not happened. Most are recorded as far as a refusal: an
unknown client reaches the error page, a bad secret earns `invalid_client` with a description, a
push with no authentication earns a bare one. Check session has no recording at all — it is
advertised, and no probe has reached it. Reproducing the refusals alone would mean
an endpoint that answers correctly for the cases nobody uses and wrongly for the case everybody
does.

**What this costs.** A client library configured against this profile discovers the document, reads
the key set, and stops at authorize with a 501 that names the reason. It cannot sign anybody in.
The first broker's profile does all of that, and is what a suite should point at meanwhile.

## Nothing outside a MitID login

<a id="outside-a-mitid-login"></a>

Revocation, introspection and device authorization answer 501.

**Why.** The platform advertises them and MitID does not reach them: none is part of a MitID
authentication, so no capture step could record one without making up a token to revoke or
introspect. They stay advertised because the document is the recording.

## Backchannel authentication

<a id="ciba"></a>

The CIBA endpoints answer 501, as the first broker's does.

**Why.** A decoupled flow needs a device to push to and a user to approve on it. Both brokers
advertise it, neither has it recorded, and a stub that answered would be inventing the one part a
test of CIBA would be checking.

## The key set is the same on every request

<a id="the-key-set"></a>

StubID publishes two keys, always the same two: the token-signing key and the request-decryption
key, in this broker's member shape.

**Why this is a divergence.** The broker's own key set is not one document. Three fetches a second
apart returned 26, 24 and 22 keys, in different orders. A client that fetches the set once and
caches it therefore fails against the broker some of the time — and passes here always, which is
the false pass this project exists not to give. Reproducing the behavior would mean inventing keys
nothing signs with.

**What this costs.** A test that asserts a client refetches on an unknown `kid` cannot be written
against this profile.

## It publishes none of its own clients

<a id="no-clients-of-its-own"></a>

`GET /_stubid/v1/clients` answers with an empty list on an instance serving this broker, and the
admin page says so where it otherwise lists three.

**Why.** The client ids StubID publishes are the first broker's, which that broker publishes for
anyone to use against its pre-production environment. This broker's own registrations are not
known: reaching one takes a login, and no login of this broker has been recorded. Handing them out
anyway would be worse than an empty list, because they are ids every route on this instance
refuses.

**What this costs.** A test that reads the roster for a client id to drive gets nothing here, which
is the honest answer until the sitting. The first broker's profile is where a client id comes from
meanwhile.

## A refusal outside the root has no body

<a id="the-404-outside-the-root"></a>

Both segments of `/auth/open` are compared exactly, what sits below them is not compared at all,
and a trailing slash below the root is refused wherever it appears — all of which the recordings
settle. What differs is the body of the refusal.

The broker answers a request below its root with an empty 404, and one outside it — the host root,
an RFC 8414 layout, a capitalized segment — with a 3428-byte HTML page from its edge, which the
application never sees. StubID answers both with an empty 404.

**Why.** The page belongs to the hosting rather than the protocol, like the `server` header and the
security headers beside it. Serving a copy of somebody's error page is the kind of imitation
TRADEMARKS.md rules out.

## An instance says it is an emulator

<a id="emulator-header"></a>

Every response carries an `X-StubID-Emulator` header, which the broker does not send.

**Why.** Everything else here exists to be indistinguishable from the real thing, which is
precisely why an instance has to be able to say what it is.

### What a recording would settle

<a id="awaiting-capture"></a>

Anything this broker's ledger is still waiting on is listed here, beside what would answer it. An
empty table means nothing is: every entry rests on a recording or on a divergence argued above.

<!-- generated:begin awaiting-capture -->
| What | What would settle it |
| --- | --- |
<!-- generated:end awaiting-capture -->

## The whole ledger

<a id="the-whole-ledger"></a>

Every annotation filed under this broker, in the order a reader needs: what is not reproduced at
all, then what diverges on purpose, then what rests on documentation, and only then what a
recording confirmed. The same list an instance serving this broker answers at
`GET /_stubid/v1/fidelity`, and the same order the admin pages put it in.

<!-- generated:begin ledger-index -->
| What | How close | On what evidence | Because |
| --- | --- | --- | --- |
| `BrokerState.ClientsFor` | OutOfContract, NotEmulated | - | [why](#no-clients-of-its-own) |
| `SignicatEndpoints.Authorize` | OutOfContract, NotEmulated | fixtures/signicat/sandbox/CAP-001, fixtures/signicat/sandbox/CAP-009, fixtures/signicat/sandbox/CAP-013 | [why](#no-login-yet) |
| `SignicatEndpoints.CheckSession` | OutOfContract, NotEmulated | fixtures/signicat/sandbox/CAP-001 | [why](#no-login-yet) |
| `SignicatEndpoints.Ciba` | OutOfContract, NotEmulated | fixtures/signicat/sandbox/CAP-001 | [why](#ciba) |
| `SignicatEndpoints.CibaCancel` | OutOfContract, NotEmulated | fixtures/signicat/sandbox/CAP-001 | [why](#ciba) |
| `SignicatEndpoints.DeviceAuthorization` | OutOfContract, NotEmulated | fixtures/signicat/sandbox/CAP-001 | [why](#outside-a-mitid-login) |
| `SignicatEndpoints.EndSession` | OutOfContract, NotEmulated | fixtures/signicat/sandbox/CAP-001, fixtures/signicat/sandbox/CAP-043 | [why](#no-login-yet) |
| `SignicatEndpoints.Introspection` | OutOfContract, NotEmulated | fixtures/signicat/sandbox/CAP-001 | [why](#outside-a-mitid-login) |
| `SignicatEndpoints.Par` | OutOfContract, NotEmulated | fixtures/signicat/sandbox/CAP-001, fixtures/signicat/sandbox/CAP-040, fixtures/signicat/sandbox/CAP-041, fixtures/signicat/sandbox/CAP-042 | [why](#no-login-yet) |
| `SignicatEndpoints.Revocation` | OutOfContract, NotEmulated | fixtures/signicat/sandbox/CAP-001 | [why](#outside-a-mitid-login) |
| `SignicatEndpoints.Token` | OutOfContract, NotEmulated | fixtures/signicat/sandbox/CAP-001, fixtures/signicat/sandbox/CAP-016, fixtures/signicat/sandbox/CAP-017, fixtures/signicat/sandbox/CAP-018 | [why](#no-login-yet) |
| `SignicatEndpoints.UserInfo` | OutOfContract, NotEmulated | fixtures/signicat/sandbox/CAP-001, fixtures/signicat/sandbox/CAP-019 | [why](#no-login-yet) |
| `SignicatKeySet.Write` | Shape, Divergent | - | [why](#the-key-set) |
| `SignicatProfile.Root` | OutOfContract, Divergent | - | [why](#the-404-outside-the-root) |
| `StubIdApplication.AnnounceTheEmulator` | Exact, Divergent | - | [why](#emulator-header) |
| `SignicatEndpoints.Discovery` | Exact, VerifiedLive | fixtures/signicat/sandbox/CAP-001, fixtures/signicat/sandbox/CAP-048 | - |
| `SignicatEndpoints.KeySet` | Exact, VerifiedLive | fixtures/signicat/sandbox/CAP-002 | - |
| `SignicatProfile.Root` | Exact, VerifiedLive | fixtures/signicat/sandbox/CAP-005, fixtures/signicat/sandbox/CAP-006, fixtures/signicat/sandbox/CAP-007, fixtures/signicat/sandbox/CAP-008, fixtures/signicat/sandbox/CAP-044, fixtures/signicat/sandbox/CAP-045, fixtures/signicat/sandbox/CAP-046, fixtures/signicat/sandbox/CAP-047 | - |
<!-- generated:end ledger-index -->
