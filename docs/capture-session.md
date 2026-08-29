# The manual capture session

One person, one browser, one sitting. This is the script for the recordings that only a
completed MitID login can produce.

Everything in `fixtures/neb/pp/CAP-001` to `CAP-019` was recorded unattended. The rest of
the broker's surface is behind an authentication that cannot be automated: the MitID widget
is a cross-origin iframe that detects and blocks browser automation, and the broker's
`simulation` bypass is not entitled on our client (see `docs/research/day-zero-probes.md`).
So a human does it once, carefully, and everything that was forgotten costs another sitting.

Read the whole document before starting. The ordering constraints are not stylistic; several
steps destroy the state a later step needs, and two of them are irreversible within the
sitting.

**Budget.** Preparation is a day of build work plus roughly forty minutes of same-day setup.
The sitting itself is 90 to 100 minutes as written, or about 70 if you take the drop list in
step 21. Six to eight of the steps require a MitID authentication at roughly three minutes each; the
rest is clicking and waiting.

---

## Part 1 — Build work that gates booking the sitting

None of this is optional and none of it can be done during the sitting. Each item was found
by reading the harness, not by guessing at it.

Status as of the last commit. The done items were fixed when this document was written, and
several of them were holes that produced a green build while publishing something they should
not have.

| | Item | Status |
| --- | --- | --- |
| B1 | The relying party | outstanding — the next piece of work |
| B2 | Request-side scrubbing | done |
| B3 | Unscrub the case URL | done |
| B4 | The signed-token fixture layout | decided below; applies when the first token is recorded |
| B5 | Structural guards for tokens and personal numbers | done |
| B6 | The hyphenated personal-number form | done |
| B7 | Stage the sitting before writing fixtures | outstanding |
| B8 | Mask cookie values, keep names and flags | done |
| B9 | Keep Content-Length honest | partly — replacements are equal-length; recomputation outstanding |
| B10 | A ClientRedirect disposition | done, with FormPost alongside it |
| B11 | A JSON request-body path | outstanding |
| B12 | Dependent cases for the two-hop error probes | outstanding |
| B13 | Keep the manual pack out of CaptureCatalogue.All | outstanding |
| B14 | Ignore session artefacts | done |

**B1. The relying party does not exist.** `Program.cs` supports `capture` and `verify` only,
`RecordingHandler` is referenced by nothing, and nothing listens on `localhost:5099`. Every
step below assumes a local RP that:

- serves a launchpad page of pre-built authorize links, one per recording, labelled with the
  step number;
- accepts the callback as **both GET and POST** (`response_mode=form_post` arrives as a POST,
  and the two are different code paths);
- tolerates a callback carrying `error=` and no `code` without throwing — most of the failure
  recordings arrive that way, and an RP that assumes a code loses the recording it exists to
  make;
- exchanges the code automatically and immediately, because codes are single-use and expire
  in seconds;
- fires a scripted follow-up battery on callback (userinfo variants, the code replay) without
  the operator typing anything, because typing eats the fifteen-minute CPR window;
- logs raw request and response bytes for everything it sends and receives;
- displays the wall-clock timestamp of the last successful login, which several steps check
  before proceeding.

**B2. Request-side scrubbing does not exist.** `FixtureStore.WriteAsync` writes the request
URL, headers and body verbatim; `RecordingHandler` scrubs the request body only. Every
`Authorization: Bearer` header, the `Authorization: Basic` header, the `id_token_hint` in a
URL and the CPR-bearing JSON bodies would land in `request.json` exactly as sent. This is the
largest hole in the sitting and it produces a green build.

**B3. `Scrubber.Unscrub` is never applied to `CaptureCase.Url`.** It runs over form values
inside `Recorder` only. Every authorize URL written as `client_id={{NEB_PP_CLIENT_ID}}` would
send the literal braces to the broker and record a puzzling refusal. Unscrub the URL when
sending and re-scrub it when storing.

**B4. Settle the JWS fixture layout.** Two surfaces proposed incompatible schemes. The
decision, which resolves it:

- Keep `response.raw` byte-exact with each compact JWS replaced by a stable placeholder
  (`{{ID_TOKEN}}`, `{{USERINFO_TOKEN}}`, `{{TRANSACTION_TOKEN}}`). The member order,
  whitespace and member positions of the *response* survive, which is what question 6 asks.
- Beside it, per token, write sidecar files holding the **decoded header and payload bytes
  verbatim**, scrubbed by text edit and never parsed and reserialised. Member order inside
  the token is the entire evidence for questions 1 and 4.
- Record in `meta.json` the `alg`, the `kid`, the JWKS certificate CN it resolved to, the
  real segment lengths, and whether the real signature verified at capture time.
- A token re-signed with the committed fixture key is a **derived** artefact, marked
  synthetic, for tests that need a whole parseable document. It is never presented as the
  recorded bytes.

What this loses: the ability to verify the real signature later. That is unrecoverable
either way once the broker's key rotates, which the transaction-signing key already did in
May 2026, so capture-time verification recorded in meta is the durable substitute.

**B5. Replace both guards.** `No_signed_token_reaches_the_repository` asserts on the literal
string `eyJhbGciOi`, which matches only a JWS whose header JSON begins exactly `{"alg":"`. A
header beginning `{"typ":` encodes to `eyJ0eXAiOi` and sails through — and the transaction
token comes from a different subsystem whose header member order is one of the unknowns this
sitting exists to settle. Replace it with a structural JWS rule. Then teach the CPR guard to
base64url-decode segments before scanning: `FindCprShapedText` requires the digits not to sit
inside a longer alphanumeric run, and a base64url segment *is* one long alphanumeric run, so
a CPR inside a `userinfo_token` can never match today.

**B6. Widen the CPR scrubber and guard to the hyphenated form.** `Scrubber.Scrub` is an exact
string replace, so a CPR returned as `310299-9995` is not matched by a redact entry holding
`3102999995`; and the guard regex requires ten contiguous digits, so it misses it too. Both
miss, the build stays green, the number ships. Register both forms and widen the pattern to
an optional separator.

**B7. Add a staging step.** Buffer the whole sitting in memory or in a directory outside the
repository, run auto-discovery plus the redact list over the **complete** set, and only then
write fixtures. Values born mid-sitting — the code, `sid`, `session_state`, every token —
appear in *earlier* exchanges than the response that names them, so write-as-you-go can never
scrub them retroactively. Replacement must be per-value stable, never per-occurrence random:
whether `sid` equals `session_identifier`, and whether `sid` is stable across a session, are
questions this sitting is paying an authentication to answer, and a fresh pseudonym per
occurrence destroys the answer while looking correct.

**B8. Mask `Set-Cookie` values, keep the names and flags.** `Normaliser` masks `Set-Cookie`
for the verify comparison only; `FixtureStore` writes the served value into `response.head`.
The committed pack already carries real `X-Correlation-Id` and `nebcausationid` values in
CAP-013. On an authenticated session that same path publishes the broker's live session
cookie, which is a credential until logout, not an identifier. The names, order, `Path`,
`SameSite`, `Secure`, `HttpOnly` and `Max-Age` are contract and must be kept; the values and
their lengths are not.

**B9. Keep `Content-Length` honest.** `ScrubBody` rewrites the body bytes, but `response.head`
still carries the served `Content-Length` and `meta.byteLength` is measured before scrubbing.
The unattended pack barely notices; the login pack scrubs nearly every body. Prefer
equal-length replacements — the CPR day-shift already is one, and a same-length fixture GUID
for the client id is better than `{{NEB_PP_CLIENT_ID}}` — and otherwise recompute both
numbers and say so in `meta.json`.

**B10. Add a `ClientRedirect` disposition.** A 302 whose `Location` is the client's
`redirect_uri` falls through to `Unclassified`, and a `form_post` 200 carrying an error is
classified as `Success`. Those are the two dispositions this sitting produces most often, and
without the enum entry every failure fixture carries the wrong disposition permanently.

**B11. Add a JSON request-body path.** `CaptureCase.Form` is form-encoded only and `Recorder`
hardcodes `application/x-www-form-urlencoded`, so the whole CPR-match API battery in step 8
cannot be sent at all. When written, it must scrub the body, because the CPR is in it.

**B12. Add a dependent-case mechanism for the two-hop error probes.** Following the 302 to
`/op/Error?errorId=…` is not enough: `FixtureStore` writes exactly one request and one
response per directory, and the `errorId` is generated per request, so the second hop's URL
cannot be a static `CaptureCase`. Without this, those probes record an opaque blob and settle
nothing. Also scrub the error page body and read what it actually renders before treating
these as safe — the page is rendered *from* that protected blob, so it is exactly where the
private client's identity or redirect URI can become readable plain text.

**B13. Take the manual pack out of `CaptureCatalogue.All`.** Both `capture` and `verify`
iterate that one list, so a routine `capture` run after the sitting would replay expired
authorization codes, overwrite the sitting's evidence with `invalid_grant`, and rehash the
manifest over the damage.

**B14. Fix the gitignore.** It covers `bin`, `obj`, `TestResults` and `capture.local.json`
only. The HARs, the RP's raw log and the scratch directory carry session cookies, real
id_tokens and the test CPR, and the guards scan only `fixtures/` and only `.json`, `.head`,
`.md` and `.raw` — so anything else in the working tree is invisible to every guard while
remaining perfectly trackable by git. Add `*.har`, the RP log and an explicit scratch path,
and put the scratch directory outside the working tree anyway. While you are in the file,
correct the stale comment describing `capture.local.json` as an "HMAC salt used to scrub
CPRs": there is no hashing scheme, only a plain string replace, and the comment would mislead
the next person into trusting a mechanism that does not exist.

**B15. Assign the CAP number ranges now.**

- `CAP-001`–`CAP-019` — the existing unattended pack.
- `CAP-020`–`CAP-049` — this sitting. Requires a human.
- `CAP-050`+ — new unattended probes, including the pre-flight batch in P4 below.

Amend the line in `fixtures/README.md` that says "CAP-020 onwards need a human" to name the
bounded range, because the new unattended probes sit above it.

### B16. The canary dry-run. Do not book the sitting until this passes.

Every one of the items above is a claim that the harness will behave. This is the only thing
that proves it. Run one complete throwaway login on the **published open code client**
`0a775a87-878c-4b83-abe3-ee29c720c3e7` with a throwaway test identity, scope
`openid mitid transaction_token`, **no `ssn`**. Seed the redact block of
`capture.local.json` with a fake CPR, a fake organisation name, a fake CVR and a fake client
id, and feed those values to the broker as `state` and `nonce` so they come back echoed.

Then attack the output. Run all three guard tests. Run `git status` and `git add -p`. Grep
every written file — fixtures, sidecars, meta, the RP log, the HAR — for each canary by hand.
A canary that survives anywhere means the corresponding item above is not actually done.

This is the only recording whose purpose is to fail, and it is the only thing standing
between a broken scrubber and an irreplaceable CPR-bearing fixture.

---

## Part 2 — Preparation

### P1. Create all three test identities, in advance

At <https://pp.mitid.dk/test-tool/frontend>, before the day of the sitting. Nothing about
these identities expires, and creating them now means every redaction is registered before
any recording, with no mid-sitting edits to `capture.local.json` and no mid-sitting harness
restarts.

- **Identity A** — the primary. Autofill, leave "Create without CPR-number" unchecked, create.
  Give it the MitID app and, if the tool offers them, a password and a code display. From the
  identity page write down the CPR, the Identity Claim (user ID) and the UUID.
- **Identity B** — sacrificial, for step 18. Autofill, create. Identity B will probably end up
  blocked, which is what it is for.
- **Identity C** — no CPR. Tick "Create without CPR-number" and give a fictitious birth date;
  the official guide uses 17.01.1907.

While you are on the create form, spend thirty seconds looking for a name- or
address-protection control. If one exists, create a fourth identity with it ticked and add a
single mitid-only login plus one userinfo call to step 20 — that would settle the
`NAVNE & ADRESSEBESKYTTET` value for real. If no such control exists, stop looking. It is a
CPR-register attribute and the test tool is not documented as exposing it.

**Write identity A's and identity B's user IDs on a piece of paper before the sitting starts.**
Confusing them is the expensive mistake of the whole sitting.

### P2. Freeze the redact list

Two things must happen first, in this order.

Capture the broker's **login page for the private client**, unattended: follow the CAP-020
authorize 302 and record the 200 at `/op/Account/Login?ReturnUrl=…`. That page is where the
broker renders the relying party's configured display name, which may differ from the legal
name, may be truncated or HTML-escaped, and may be accompanied by a logo URL or a support
address that names the organisation. `Scrubber.Scrub` is a plain string replace, so it only
removes the exact rendering it was handed, and the shipped example file guesses
`Example A/S` / `12345678`. This capture is the only way to freeze a complete list before a
login, and it is a fixture StubID needs for M3 anyway.

Then write the whole list into `capture.local.json` under `redact`:

- Identity A's CPR, **in both forms** — contiguous and hyphenated — mapped to the same ten
  digits with the day shifted by +60.
- Identity C's CPR if the tool issued one (it should not have).
- Two **pre-chosen wrong CPRs** in replacement form (day +60), for step 8 and step 19. Do not
  improvise these at the keyboard: roughly ten million CPRs are allocated, so a plausible
  birth date plus four digits has a high chance of being a real person's number. If MitID
  rejects the replacement format, that rejection *is* the recording; only then fall back to a
  number whose century digits encode an 1800s birth year, and never to a plausible modern one.
- Identity A's and C's name and address as **fixed fictional replacements of the same length**,
  so what gets published is a deliberately incoherent dossier rather than a coherent fake
  person that will be scraped and reused.
- Every rendering of the organisation name found in the login page capture, and the CVR both
  bare and DK-prefixed.

**Restart the harness after this edit.** `LocalSettings` caches `capture.local.json` in a
static `Lazy<JsonDocument>`, so a value added to a running process is never redacted. That is
a silent disclosure, not a red build.

### P3. Why the CPR is published rather than replaced with a placeholder

The number belongs to a synthetic MitID pre-production identity. After the day-shift it is a
replacement number in the 61–91 day range, which by construction cannot be any living
person's CPR — which is exactly why `Scrubber.CprPattern` ignores that range. The broker's own
`simulation` parameter is documented as taking `cpr:<replacement cpr>`, so replacement numbers
are in-domain for this system. The wire format — ten bare digits versus a six-four form with a
separator — is a byte StubID has to reproduce and is unrecoverable from an opaque placeholder,
and an unpublished fixture is an unverifiable one, which breaks the project's whole claim for
outside contributors.

Captured but **never committed**, whatever else happens: HARs, the relying party's raw log,
the compact access, refresh and id tokens, and all cookie values.

### P4. Run the unattended pack, including everything new

None of this needs a human, so none of it belongs in the sitting. Get it recorded and green
first, so nothing is discovered during paid time.

Re-run `CAP-001`–`CAP-019`, then add and run, numbered from `CAP-050`:

- **The response_type refusals.** `code id_token` with `response_mode=form_post` on the private
  client, and the same on the published open implicit client
  `9d3c7d79-96c4-43bc-8562-f0bf88ef69b8`. Both answer `unauthorized_client`. Follow each 302
  and record the error page as a second hop — the page body carries the OAuth error code in
  plain text, which is what makes these evidence rather than an opaque blob. Mask the
  `errorId` and `session_state` as volatile or every verify run shows a false diff.
- **`offline_access`** → `invalid_scope`. No refresh token exists, so nobody should spend
  operator time looking for a refresh-grant response.
- **`response_type=id_token` with no `nonce`** on the implicit client → `invalid_request`.
- **`prompt=none` with no session** on the private client → a 302 **back to the client**:
  `?error=login_required&state=…&session_state=<43 chars>.<32 uppercase hex>#_`. Parameter
  order is error, state, session_state; there is no `error_description`; there is no `iss`
  despite discovery advertising `authorization_response_iss_parameter_supported: true`; and
  there is a literal trailing `#_`. This contradicts the rule the first pack established that
  an invalid authorize never redirects back — interaction failures do. Record the same request
  with `idp_values` omitted, confirm it makes no difference, and note that in meta rather than
  committing a second fixture. Record it once more with a garbage `id_token_hint`, which is
  also ignored entirely.
- **The `form_post` error envelope**, once. `prompt=none` plus `response_mode=form_post`:
  200 `text/html; charset=UTF-8`, ~570 bytes, an auto-submitting form with single-quoted
  attributes, `<base target='_self'/>`, one newline between hidden inputs,
  `<noscript><button>Click to continue</button></noscript>` and a load-event auto-submit. Note
  the `x-content-security-policy` header (the old header name) with its sha256 script hash and
  `referrer-policy: no-referrer`. Capture this on **one** client only; the two variants
  proposed differ by three bytes, which is the length of the `state` value.
- **`prompt=bogus`** → `/op/Error`, so the client sees nothing. An unrecognised prompt value
  and an unsatisfiable one use two different refusal channels on the same parameter.
- **`prompt=select_account`**, which is in the broker's own `prompt_values_supported` and which
  nobody had probed.
- **`endsession` with no session**, bare and with `id_token_hint=not-a-token` plus a
  `post_logout_redirect_uri`: a 302 to `/op/Account/Logout` with no query string and an empty
  body in both cases. Note that `Cache-Control` here is `max-age=0, no-cache, no-store`, a
  different member order from the authorize responses' `no-store, no-cache, max-age=0`. Then
  `GET /op/Account/Logout` itself: 200, ~7.9 KB, the broker's own logged-out page.
- **The login page and the identity-provider chooser**, as committed recordings: the
  `/op/Account/Login?ReturnUrl=…` 200, the same flow with `idp_values` omitted, and the
  internal `/op/connect/authorize/callback` path. Every existing fixture stops at the 302, so
  the front-channel page sequence StubID must serve at M3 is captured nowhere.
- **A pushed authorization request with client authentication**, then an authorize using the
  returned `request_uri`. Discovery advertises the endpoint; CAP-019 records only the
  unauthenticated refusal. This settles the `request_uri` format, `expires_in`, member order,
  and the error when a `request_uri` is reused or has expired.
- **Existence probes for `/op/connect/revocation` and `/op/connect/introspect`.** Discovery
  publishes `revocation_endpoint_auth_methods_supported` and
  `introspection_endpoint_auth_methods_supported` but neither endpoint URL. If they exist, add
  the two ride-alongs marked in steps 12 and 21.
- **Userinfo with a well-formed but unknown bearer token**, which CAP-017 does not cover.

Finally, **smoke-test the RP's callback handler** against two of these before the sitting: the
`login_required` redirect (a GET) and the `form_post` error envelope (a POST). They are
different code paths, both are free and unlimited, and both must be proven before an
authentication is spent on them.

### P5. On the morning of the sitting

Run `dotnet run --project tools/StubId.CaptureHarness -- verify`. Step 11 resolves token
`kid`s against the committed CAP-002 JWKS, so the JWKS it resolves against must be same-day.
A `kid` that resolves to nothing means a rotation happened, which is data rather than a
failure, but it makes CAP-002 stale.

### P6. The browser matrix

- **Profile 1** — the main browser profile. Every foreground recording, from step 2 to step 16.
  Never clear cookies in it during the sitting.
- **Browser 2** — a *different browser application*, not a second profile and not an incognito
  window; those share a cookie jar and one parked flow will clobber another. Used only for the
  timeout measurement in step 1.
- **Profile 3** — a fresh profile for identity C, step 19.
- **Profile 4** — a fresh profile for identity B, step 18.

Disable display sleep and system sleep for the whole sitting. Open devtools in profile 1 with
Preserve log ticked.

### P7. Conventions for every step

`state` is the step's name. PKCE S256 throughout, using the RFC 7636 example pair so the
fixtures stay reproducible: verifier `dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk`, challenge
`E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM`. PKCE is not required by this client, but a
stock client sends it and it costs nothing. Redirect URI `http://localhost:5099/callback`.
Client is the private one throughout, except where a step names the open implicit client.

HARs: export **without content** everywhere except the two steps where a page body is the
finding. A HAR carries the entire cookie jar including the broker session cookie, every
`Authorization` header, every token in every response body, the CPR as typed into MitID's
form, and the identity's user ID, and nothing scrubs it. Write HARs to an absolute path
outside the repository tree.

Anything you transcribe from a screen goes through the redact list before it reaches
`meta.json`. Broker and MitID screens address the user by name, and `meta.json` is written
straight from the case with no `Scrub` call. No screenshot of a MitID screen enters the tree.

---

## Part 3 — The sitting

### Step 1. Park the abandoned login (browser 2)

Start this first so its clock runs underneath everything else.

In **browser 2**, open devtools with Preserve log on and click the launchpad link for
`state=timeout-widget`: the plain authorize with `idp_values=mitid`. Stop as soon as the MitID
widget has rendered and is asking for a user ID. Type nothing. Write down T0 to the minute.

Leave the window **open and on screen** — not minimised, not behind another window, not a
background tab. Browsers throttle timers in hidden tabs and would corrupt the measurement.

Glance at it at T+5, T+10, T+15, T+20, T+30 and T+45, and note the clock time of any visible
change. Also check the RP log at each glance: the expiry may arrive as a callback with no
visible cue. **Skip any glance that falls inside the fifteen-minute CPR window opened at step
8** and note that you skipped it; that window matters more.

*Settles:* question 10, which has no documented answer at all. The honest answer to the
duration is the checkpoint schedule, not a number.

*This went wrong if:* the flow is running in the same cookie jar as anything else. A later
login can consume or replace this flow's context and the measurement becomes an artefact.

### Step 2. Abort inside the MitID widget (profile 1)

No identity is touched, nothing is typed, and it costs about forty seconds. Run it first among
the foreground recordings, as a rehearsal of the HAR-plus-RP discipline while a mistake is
still free.

Clear the network list. Click the launchpad link for `state=abort-mitid`. Wait for the widget
to render. Click the widget's own abort control — the "Afbryd" / "Cancel" link beneath the
user-ID field — and confirm if a dialogue asks. Let the browser land wherever it lands. If it
lands back on a broker page rather than at localhost, keep clicking the cancel or return
control until the browser leaves `pp.netseidbroker.dk`, and record every hop. Export the HAR
under this step's name and confirm the RP logged a callback for `state=abort-mitid`.

*Settles:* question 5, the base case. Expect `error=access_denied` with
`error_description=mitid_user_aborted`, the one combination the vendor documents. Check
explicitly whether the failure redirect carries the same furniture as the `login_required`
redirect already recorded: parameter order, `session_state`, the trailing `#_`, and whether
`iss` appears — it was absent on `login_required` despite discovery advertising it.

*This went wrong if:* the abort control returns you to the broker's identity-provider chooser
instead of to the client. Then the recording measures the widget's cancel, not the flow's
abort. If the browser never leaves the broker at all, record that as the finding and note
which page it stops on.

### Step 3. Browser-back, in the same tab

This cannot be reordered away from step 2; it reuses that tab.

With Preserve log still on, press Back twice, until a broker page from the middle of the flow
re-renders. Click the primary control on it, or resubmit the form. Record where it lands. If
Back yields only a cached page with no request at all, reload once to force one. Export the
HAR under *this* step's name so it is not confused with step 2's.

*Settles:* question 5 for the navigation family, which is three catalogue codes with no
documented OAuth error value: `mitid_anti_forgery_validation_error`, `user_navigation_error`,
`user_navigation_error_empty_state`, and possibly `mitid_auth_code_already_used`. Browser-back
is the failure real users cause most often.

*This went wrong if:* the RP log entries are indistinguishable from step 2's. Compare by
timestamp before concluding anything; if the broker simply replayed the previous error, record
*that* as the finding rather than discarding the step.

### Step 4. Abort at the broker's own step

Same profile. `state=abort-broker`, with **`idp_values` omitted** so the broker shows its own
identity-provider chooser instead of handing straight to MitID.

Stop on the chooser. Do not pick MitID. Click the broker's own cancel control — "Fortryd" /
"Annuller" / "Tilbage til tjenesten" — on the broker's chrome, not inside any iframe. Export
the HAR and check the RP log.

*Settles:* whether the broker's plain `user_aborted` code is reachable and distinct from
`mitid_user_aborted`. The catalogue publishes both and StubID has to know which step produces
which.

*This went wrong if:* no chooser appears (the broker auto-selects MitID when only one provider
is available) or the chooser has no cancel control. Either is a legitimate negative — record
what is on screen and move on. Do not improvise a different abort, because an abort taken
somewhere else is silently step 2 again. If there is no cancel control, the browser Back
button is the closest substitute, but log it as a substitution: it will produce a navigation
error code, not an abort code.

### Step 5. Malformed `uuid_hint`, both response modes

Two clicks, no identity, under two minutes. Run (a) first.

(a) `state=uuid-hint-query`, with
`idp_params={"mitid":{"uuid_hint":"not-a-uuid"}}` percent-encoded — byte-identical to
CAP-010's, which already pinned that this request is *accepted* at authorize.
(b) The same with `state=uuid-hint-formpost` and `&response_mode=form_post`.

With `idp_values=mitid` there is usually nothing to click; the flow should fail at or just
after the handoff. Record where it lands: back at localhost, or on `/op/Error`. For (b), check
that the RP logged the POST **body bytes** and not just a query string.

*Settles:* question 5 for the parameter-level in-flow failure. Expect
`error_description=mitid_uuid_hint_malformed`; the OAuth error value itself is unknown, and
`access_denied`, `invalid_request` and "no client redirect at all" are all live possibilities.
Also the `form_post` wire shape for an error that carries a description — the free probe
settled the three-input form for `login_required`, and whether `error_description` becomes a
fourth input, and where it sits in the order, is unrecorded. Stock ASP.NET Core uses
`form_post` by default, so this is the shape the M3 client will parse.

*This went wrong if:* the widget asks for a user ID. Then the hint is being ignored — abort and
record that as the finding. If (a) lands on `/op/Error` and never reaches the client, skip (b):
`/op/Error` ignores `response_mode` and it would produce nothing new.

### Step 6. The baseline login (identity A)

This is the canonical minimal-scope authentication. Three surfaces each proposed their own
version of it; they are merged here so their id_tokens are comparable.

```
GET /op/connect/authorize
  ?client_id={{NEB_PP_CLIENT_ID}}
  &response_type=code
  &scope=openid%20mitid
  &redirect_uri=http%3A%2F%2Flocalhost%3A5099%2Fcallback
  &state=cap020&nonce=cap020-nonce
  &idp_values=mitid&language=en
  &code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM
  &code_challenge_method=S256
```

No `idp_params`, no `prompt`, no `acr_values`, so the broker applies its default assurance
level, which is Substantial. Token POST authenticated with `client_secret_post` — what a stock
ASP.NET Core client sends.

Complete the login with identity A: type the Identity Claim, choose "MitID app", then open the
App simulator on the test-tool identity page and click "Scan QR and confirm". Use the
**ordinary approval**, not the enhanced or biometric one. **Do not tick any remember-me or
trusted-device option** — a persistent device trust changes later logins in ways nothing here
records.

**Write down the wall-clock minute at which MitID completed.** That starts the fifteen-minute
window in which MitID allows a CPR match. Steps 7, 8 and 12 all live inside it.

The RP then runs, automatically and immediately:

1. `GET /op/connect/userinfo` with the bearer token;
2. the same with `Accept: application/jwt`;
3. `POST /op/connect/userinfo` with the bearer token and an empty form body;
4. **the code replay** — re-POST the same, already-exchanged authorization code to the token
   endpoint. CAP-015 records only a fabricated code, which is a different case: IdentityServer
   revokes the whole grant on replay. A double-submitting browser or a retrying proxy hits this
   constantly and StubID would currently guess;
5. `GET /op/connect/userinfo` again with the same access token, which settles whether the
   replay revoked the tokens already issued.

Keep this access token. Step 7 replays it.

*Settles:* question 1 in full — the JOSE header member set and order (which is the assumption
currently carrying `AwaitingCapture = "CAP-020"` on `JwsWriter.Sign`, where `alg/kid/typ` is a
guess and `x5t` may well be there), the payload member set and order, whether `nbf` and `sid`
really appear, whether `idp_environment` is present when documented as optional, whether
`at_hash` appears on a token-endpoint id_token, and the JSON type of `auth_time`. Question 2
for the default authenticator — both the claim name (`amr` or `mitid_amr`) and the value form
(bare `code_app`, dot-prefixed `mitid.code_app`, or colon-prefixed `mitid:code_app`; a
third-party broker documents bare values for MitID and colon-prefixed ones for MitID Erhverv,
so all three are live). Question 6, the minimal-scope token response that step 7 is diffed
against. Question 8 for a minimal claim set — status line, Content-Type, minified or indented,
member order, and which of the three documented session-claim spellings the wire actually
uses (`session_status` + `session_identifier`, or `session_expiry` + `session_is_active`).
Whether `mitid.has_cpr` appears at all with no `ssn` scope. Whether userinfo answers POST, and
whether `Accept: application/jwt` switches it to a signed response.

*This went wrong if:* the RP did not exchange the code within seconds — the login is wasted and
must be redone. Or the browser held an existing broker session, making this an SSO recording
rather than a fresh authentication; that is what the fresh profile is for.

### Step 7. The full-scope login with the CPR (identity A)

Immediately after step 6, **same browser window, no cookie clearing, no `prompt` parameter**.
Target: within ten minutes of the MitID completion you noted.

```
scope = transaction_token ssn openid userinfo_token mitid
        ssn.details_address nemid.pid ssn.details_name
state = cap021, fresh nonce, same PKCE pair
```

The scope order is scrambled on purpose — all eight granted scopes with `openid` deliberately
not first. This exact string was probed and accepted, so whatever comes back tells us whether
the broker echoes the request verbatim or re-orders it canonically. That is a byte StubID has
to reproduce and it cannot be guessed.

Token POST authenticated with **`client_secret_basic`**, so both advertised client
authentication methods get recorded across the two logins. **Conditional:** keep this variant
only if B2 is done and the canary proved that request headers are scrubbed. `Scrubber.Scrub`
replaces the raw secret and its percent-encoded form, neither of which matches
`base64(id:secret)`, and `FindUnscrubbedCredential` keys on field names an `Authorization`
header does not contain — so without B2 this is the highest-risk byte in the sitting for the
smallest finding. If B2 is not done, use `client_secret_post` and note the substitution.

Before clicking: **watch the screens**. This is the first fat-scope authentication and whatever
the broker shows for the first time may never reappear for this identity. Note whether MitID is
invoked again at all, whether a consent screen appears, and the exact wording of the CPR
prompt. Export a HAR **with content** for this step only, and keep it outside the tree. StubID
has to reimplement these screens; paper is not a recording.

Type identity A's CPR from the test tool. **Type it correctly the first time** — a wrong entry
here costs the success recording and burns one of three match attempts.

The RP then runs automatically: `GET /op/connect/userinfo`; `POST /op/connect/userinfo` with an
empty form body; and `GET /op/connect/userinfo` **with step 6's older access token**. Keep the
id_token, access token, `userinfo_token` and `transaction_token`.

*Settles:* question 3 in full — the wire type of every personal-data value. Whether `dk.cpr`,
`mitid.age` and `mitid.has_cpr` are JSON strings; whether
`ssn.details.name_address_protected` is the boolean the identity-provider page implies or the
string `"false"` the SSN Details API page shows; whether `ssn.details.person_status` is
lowercase or PascalCase, a contradiction inside the vendor's own documentation; and whether
members with no value are emitted as `null` or omitted. What `dk.cpr` contains for a private
service provider, and its wire format — ten bare digits or a six-four form with a separator.
Question 8 in full, on the fat response. Question 6's additions: what `userinfo_token` and
`transaction_token` add to the token response, where they sit in the member order, whether
`scope` is echoed and in which order. Question 1's direct form: whether seven extra scopes
change the id_token at all, or whether the broker keeps identity claims out of it and only
userinfo grows — a clean diff against step 6, because the client, identity and authenticator
are identical and scope is the only variable. Question 4's transaction-token questions on a
CPR login, including whether `transaction_actions` contains a `cpr_match` or `cpr_lookup`
action alongside `mitid.login`, and whether `dk.cpr` reaches the transaction token when the
client is not configured to require it. And, undocumented and observable only here: whether
userinfo claims are bound to the access token or to the session — step 6's older token
replayed after the upgrade either returns the minimal claim set or the upgraded one, and
StubID must pick one.

*This went wrong if:* more than ten minutes have passed since step 6's MitID completion. Re-run
this authorize with `prompt=login` and authenticate fresh; the recording stays valid and only
the session-reuse observation is lost. Or the whole scope pile is refused at authorize — fall
back to `openid mitid ssn userinfo_token transaction_token` and take the two `ssn.details_*`
scopes in a third authorize, and **record the refusal too**, because a rejected scope
combination is a fact. If the scrambled order specifically is refused, retry once with `openid`
first and the rest still scrambled, and note the retry: a refusal is itself the answer.

*Expect and do not fight:* `ssn.details.status` and `nemid.pid_status` will very likely come
back `unable_to_lookup`, because a test-tool CPR need not exist in the pre-production CPR
register. That is a legitimate recording of a shape StubID must emit. Do **not** burn another
identity hunting for one that resolves; mark the success shape of `ssn.details.*` as
documentation-only in the fidelity ledger.

### Step 8. The CPR-match API battery

No browser. Runs immediately, inside the same fifteen-minute window, on step 7's access token.
Every call carries `Authorization: Bearer` and `Content-Type: application/json`.

The attempt budget is three per session and **step 7 already spent one**, so the correct CPR
must go first or the true branch stays unrecorded.

1. `POST /op/api/v1/mitid/matchCpr` `{"cpr": "<identity A's correct CPR>"}`
2. `POST /op/api/v1/mitid/matchCpr` with the **first pre-chosen wrong CPR** — the mismatch shape
3. `POST /op/api/v1/mitid/matchCpr` with the **second pre-chosen wrong CPR** — expect the
   exceeded refusal
4. `POST /op/api/v1/mitid/nemidPidLookup` `{"cpr": "<correct CPR>"}`
5. `POST /op/api/v1/tokenverify` `{"idToken": "<step 7's id_token>"}`
6. If P4 found an introspection endpoint: introspect step 7's access token.

*Settles:* whether `cprNumberMatch` is the JSON boolean the pre-production swagger declares or
a string like everything on the userinfo side, what else is in the object, and which
Content-Type is actually served (the swagger offers `application/json`, `text/json` and
`text/plain` for the same 200). The undocumented refusal when the three-attempt limit is spent
— status code and body for the case the vendor states only as prose. Whether
`nemidPidLookup` really answers with `CprMatchResponse` as the swagger claims or with the PID
it is named for; the schema reads like a copy-paste error. Whether `tokenverify` returns every
claim coerced to a string, which is the question-3 typing thesis tested at a second endpoint.

*This went wrong if:* the exceeded refusal arrives one call earlier than the sequence implies.
That is expected, not a failure — every call is a fixture wherever it lands. Do not re-run the
battery to make it line up. A 403 on `matchCpr` or `nemidPidLookup` is also a recording: it
pins how these endpoints refuse an authenticated but unentitled caller, next to CAP-018's
anonymous challenge.

*Note:* the request bodies carry the CPR, so `request.json` goes through the same day-shift
redaction as the responses. Without B11 these calls cannot be sent at all.

### Step 9. The transaction token with a reference text

This needs its own authentication and **must carry `prompt=login`**. Running it after step 6 in
the same profile without it means SSO skips the MitID approval, the reference text is never
displayed, and the transaction token records an SSO reissue — which is step 12(a)'s question,
not this one's.

```
scope = openid mitid transaction_token
state = txn1, fresh nonce, prompt=login
idp_params = {"mitid":{"reference_text":"U3R1YklEIHJlZmVyZW5jZSB0ZXh0IG9uZQ=="}}
```

That value is base64 of `StubID reference text one`. Its SHA-256 is
`9324bb117ed5b87771f29bffd4dcd1405850cce18a6806e90bf4a659ff2698b2`, so the fixture can tell
passthrough-base64 from decoded text from a digest.

**Deliberately no `ssn` scope**, so no CPR enters this recording. Complete the login with the
app simulator and the ordinary approval. Note which authenticator you approved with, and
**whether the reference text was actually displayed**.

**Then stop and look at the token response before anything else: is there a `transaction_token`
member?** The scope passing authorize does not prove the feature is switched on for the client.
If it is absent, the transaction-token surface is dead for this sitting — abandon step 12's
transaction ride-along, report it, and do not spend the sitting hunting for it.

*Settles:* question 4, nearly all of it. `identitytype` versus `identity_type`. Whether `loa`,
`aal`, `exp`, `aud` and `nbf` are present — one source lists none of them, the worked example
carries all five plus `acr` and `ial`. The JSON type of `auth_time`, typed as a string in the
worked example while `iat`, `nbf` and `exp` are numbers in the same token; a stub emits all
four as numbers by default and would be wrong. Whether `spec_ver` is present and its value.
`recipient_info`'s real shape — object or JSON-encoded string, flat dotted member names or
nested, and whether a top-level `redirect_uri` appears instead. Whether `transaction_actions`
is present on an ordinary login, and whether a single action is a bare string or a
one-element array — comparing against step 7's multi-action CPR login decides that pair.
`mitid.reference_text` versus `mitid.referencetext`, which is the half of the naming
contradiction that is decidable without `signtext_api`, plus whether the value comes back as
the base64 we sent, the decoded text, or a digest. Whether `mitid.psd2` is issued and whether
it is the string `"false"` or a boolean. Whether `signing_cert_ocsp_nonce` appears on a
login-only transaction token. The full member set and member order, which no document states
anywhere. And question 2 as a cross-check: one token response carries both an id_token and a
transaction token, so this settles whether the two disagree with each other about `amr` on a
single login.

*This went wrong if:* the authorize lands on `/op/Error`. The `idp_params` JSON is almost
certainly mis-encoded — drop `idp_params` and retry immediately rather than debugging. The
plain login still settles everything except the reference-text naming.

### Step 10. Key binding, verified in the chair

No browser, no new request. Run the prepared script on step 9's token response against the
committed CAP-002 JWKS. **Write and test this script before the sitting**, against a hand-made
JWS, or it will be debugged instead of run.

It must, for every JWS in the sitting so far, not just the transaction token:

1. base64url-decode the JWS header and print `kid`, `alg`, `typ`;
2. resolve each `kid` against CAP-002 and print the subject CN of the resolved `x5c`
   certificate;
3. **verify the RS256 signature** against the resolved public key, so the binding is proved
   cryptographically rather than read off a header;
4. if `transaction_token_ocsp_resp` is present, DER-decode it, assert its CertID matches the
   NEB Transact certificate, and record the status and `producedAt`;
5. assert the transaction token's `nonce` equals the nonce sent, and print the payload member
   order and each value's JSON type.

Expect `transaction_token` to resolve to `CN=NEB Transact PP`
(`7FF447FA0FB65A7E749E8B43AC635862381F0CC3`) and `id_token` to
`CN=Nets eID Broker Token Signing 1 PP Env` (`048058BB59F4D3007045896FD488CE81F4EB4923`).
**Assert the binding, do not assume it.**

*Settles:* that the transaction token is signed by a different key than the id_token and
exactly which published key signs which — a fact previously believed wrong. That the
transaction-signing key is inside the JWKS rather than distributed out of band: the worked
example's `kid 20595A4B…` matches none of today's three keys, which is what made the key look
external; it is a rotated thumbprint. Whether `transaction_token_ocsp_resp` is really served
and is an OCSP response over the transaction-signing certificate. Whether the `nonce` claim,
listed by one source and omitted by another, is present.

*This went wrong if:* a `kid` resolves to nothing. That means a rotation happened between P5's
verify pass and now — recordable data, not a failure, but note it and re-run verify. Do not skip
the signature verification: a header `kid` alone would not have caught the belief this check
exists to correct.

### Step 11. Assurance level Low (identity A)

`state=cap023`, `prompt=login`, `scope=openid mitid`,
`idp_params={"mitid":{"loa_value":"Low"}}` — exactly that, no spaces, percent-encoded.
`prompt=login` is load-bearing: without it an SSO session hands back step 6's authentication
and step 6's `amr`, and the recording settles nothing.

At the MitID widget, pick the **lowest-friction authenticator on offer**, in this order:
password alone, then code display (kodeviser), then the app. A code-display login is password
plus a code, so if it is offered it should produce **two entries in `amr`**, which is the more
valuable outcome. Do not abort if password is not offered — complete with whatever the widget
gives you and record it. What MitID offers at Low is itself the finding.

**Before moving on, decode the id_token and check two things:** that `loa` really came back as
`Low`, and that **`auth_time` actually moved**. `loa_value` is not validated at the authorize
endpoint — Low, Substantial, High, lowercase `low` and the nonsense value `NotALoa` all reach
the login page unchanged, exactly as CAP-010 found for `uuid_hint` — so a misspelling costs a
whole authentication and only shows up afterwards. And `prompt=login` forces the *broker* to
re-authenticate but does not necessarily force *MitID* to; if MitID keeps its own session,
`amr` and `auth_time` can still describe step 6 while everything looks correct.

*Settles:* question 2's second and possibly third value, and the shape question step 6 cannot
answer — whether `amr` is a single-element array or genuinely multi-valued. Question 1's
assurance claims: the `loa`, `aal` and `ial` values at Low, their exact URI form, and whether
any id_token member appears only at a particular assurance level, which is the sort of thing
that makes a stub work at Substantial and break at Low.

*This went wrong if:* `loa` is not `Low`, or `auth_time` did not move. In the first case the
recording is mislabelled rather than useless; in the second it is step 6's authentication
wearing a new state value, and it settles nothing about `amr`.

### Step 12. The single sign-on sequence

Requires a completed login by the **same client, in profile 1, minutes old** — step 11's
provides it. Do not clear cookies at any point. All four carry a fresh nonce and fresh PKCE.

(a) `state=sso-second-login` — the plain base authorize, **no `prompt`**. Add
`transaction_token` to its scope with a fresh nonce, which is the transaction surface's SSO
question and costs nothing here.
(b) `state=silent-none` — `&prompt=none`.
(c) `state=silent-hint` — `&prompt=none&id_token_hint=<the real id_token from step 11>`.
(d) `state=max-age-zero` — `&max_age=0`. OIDC makes `auth_time` REQUIRED in the id_token when
`max_age` is used, and nothing else in the plan touches it.

For any of these that returns a code, confirm the RP exchanged it and captured the token
response **before clicking the next one**. The codes are short-lived; do not batch this.

*Settles:* question 9's session half. Whether the broker maintains an SSO session for this
client at all — and **either answer is large**. If (a) returns a code with no clicks, StubID
must implement SSO or every silent-renewal test will diverge. If (a) shows the widget again,
then MitID re-authenticates on every authorize and `prompt=none` can never succeed against
this broker, which means an RP developer's silent renewal is permanently broken in production
too, and StubID must reproduce that rather than being helpfully more capable than the real
thing. Whether `prompt=none` behaves differently with a *real* `id_token_hint` than without
one — the free probe proved a garbage hint is ignored outright, so if (c) differs from (b) the
hint is parsed only when it validates. Question 1 at no extra cost: comparing a silently
re-issued id_token against step 11's shows whether `sid` is stable across authentications,
whether `auth_time` stays at the first authentication or moves, and whether `amr` and
`idp_environment` are re-emitted identically. Question 4: which `transaction_actions` value a
login that did not re-authenticate produces — `mitid.sso_login` or `mitid.reuse_jwt`, both
documented without saying when each applies — and whether a transaction token is issued at all
without a fresh approval.

*This went wrong if:* the profile, the client or the cookie state is not what you think.
Every one of those turns every answer into `login_required`, which looks exactly like a
genuine finding. Check the RP's last-login timestamp before starting. If (a) re-authenticates,
(b) and (c) are near-certain `login_required` and can be run quickly.

*Note:* (c) puts a real id_token in a request URL. Without B2 this cannot land as a fixture,
though it can still be recorded.

### Step 13. The API logout, and the proof it worked

Back-channel, no browser. This is one half of the double-booking that neither surface resolved:
both the API logout and the `endsession` recording claimed the last live session and each
destroys it. They are resolved here by putting the API logout first and letting step 14's
already-planned `prompt=login` mint a fresh session for step 16.

1. `POST /op/api/v1/session/logout` `{"id_token": "<the newest id_token from step 12, or step
   11's if step 12 returned none>"}`. No bearer is required.
2. Immediately: the base authorize with `&prompt=none&state=post-api-logout`. Expect
   `error=login_required`, which is the only evidence the session actually died rather than
   merely being redirected away from.
3. `GET /op/connect/userinfo` with the access token from that same session.

*Settles:* whether the API logout terminates the session at all. And the undocumented
post-logout behaviour of userinfo: whether the access token still answers after the session is
terminated, and if it does, whether the session claim flips (`session_is_active: "false"`, or
`session_status` something other than active) or the whole personal-data set disappears. This
is the only cheap way to see the session claims in a non-active state.

*This went wrong if:* step 2 still returns a code. Then the API logout did not destroy the
session, which is itself the finding — record it and continue; step 16 will still have a live
session either way.

### Step 14. Assurance level High, enhanced approval (identity A)

`state=cap024`, `prompt=login`, `scope=openid mitid`,
`idp_params={"mitid":{"loa_value":"High"}}`. This step does double duty: it records the
enhanced-approval `amr` value *and* it re-establishes the live session that step 16 needs, so
it costs nothing net.

Authenticate with the MitID app and, at the approval step in the code-app simulator, choose the
**enhanced** option — the one asking for a PIN or biometric rather than a plain tap. Check the
simulator actually offers that path before spending the authentication; without it this login
yields the same `code_app` already recorded in step 6 and settles nothing new.

Decode the id_token and confirm `loa` came back as `High` and `auth_time` moved, exactly as in
step 11.

*Settles:* question 2's single most fragile byte — whether the broker really emits
`code_app_enchanced`, with the misspelling that appears in its own documentation and in
third-party documentation of the same values. If StubID spells it correctly and the broker does
not, every client matching on that string breaks against the real thing, and no amount of
reasoning settles it. Question 1's `loa`/`aal`/`ial` at High, completing the range alongside
step 6 at the default Substantial and step 11 at Low.

*This went wrong if:* the simulator offers no enhanced path, or `loa` did not come back High. In
the first case, skip it and say so; in the second the `loa_value` spelling was wrong.

### Step 15. Front-channel id_token, open implicit client

**This is the last step that may touch profile 1's cookie jar before logout, and it must come
after step 12**, because it introduces a second client into that jar and would otherwise turn
the SSO question into a cross-client question. Everything from here on that concerns the private
client's session is already recorded.

```
client_id = 9d3c7d79-96c4-43bc-8562-f0bf88ef69b8   (the broker's published open implicit client)
response_type = id_token
response_mode = form_post
scope = openid mitid
state = cap022, nonce = cap022-nonce
```

`nonce` is mandatory here; omitting it is `invalid_request`. `response_mode=form_post` is not
optional for us — under the default fragment mode the id_token never reaches the server and
the RP cannot record it. If the broker keeps an SSO session, this costs zero clicks; otherwise
authenticate with the app and the ordinary approval.

Record the **raw POST the browser makes to the callback** — the full form body, with the
id_token value preserved exactly — and the broker's HTML page that produced it. There is no
token endpoint call in this flow. Nothing here needs scrubbing: it is the broker's own published
client.

*Settles:* question 7 as far as this broker permits. The populated `form_post` envelope, byte
for byte — the same shell the free probe captured, now carrying `id_token`, `state` and
`session_state` instead of an error. That envelope is what StubID must emit for any
front-channel response and it is the half of question 7 that is actually recordable. Question 1
for a front-channel id_token: its JOSE header, its payload member set and order, that `at_hash`
and `c_hash` are both absent (no access token, no code), whether **`s_hash`** appears when
`state` is sent — `HashClaims` already anticipates it and nothing has ever confirmed it exists
— and that `nonce` is echoed. And the configuration question behind M3: `response_type=id_token`
with `form_post` is what an ASP.NET Core `OpenIdConnect` handler sends when nobody sets
`ResponseType`, so this is the exact exchange StubID must answer to let a default-configured app
sign in.

*Caveat to write into the fixture's meta:* this is a different client from step 6 — an
age-verification test client with its own claims configuration and allowed scopes. A difference
in *member set* between step 6's id_token and this one cannot be attributed to front-channel
versus back-channel. What survives that confound is the envelope, the hash-claim absences,
`s_hash`, the nonce echo and the header shape.

*Do not* repeat this with `response_mode` omitted and the fragment copied out of the address
bar by hand. It puts a real unexpired id_token on the clipboard with no scrubbing path, to
confirm values this recording already delivers.

### Step 16. End session. Terminal for profile 1.

Nothing session-dependent runs after this in profile 1.

(a) `GET /op/connect/endsession` **with no parameters at all**. Capture the redirect chain and
whatever page renders. **Do not click any confirmation button on it.**
(b) Immediately: the base authorize with `&prompt=none&state=post-logout-check-1`. If it returns
`login_required`, the bare endsession logged the user out without asking — record that as the
finding, and **insert one fresh login with identity A** before continuing, because the rest of
this step needs a live session. If it still returns a code or the widget, the session survived
and (a) was a confirmation prompt; leave it unconfirmed.
(c) `GET /op/connect/endsession?id_token_hint=<the id_token from step 14, or the newest live
one>&post_logout_redirect_uri=http%3A%2F%2Flocalhost%3A5099%2Fsignout-callback-oidc&state=logout-hint`.
Follow the whole chain: expect `/op/Account/Logout`, possibly with a `logoutId`, and then either
a redirect to localhost carrying `state=logout-hint` or the broker's own signed-out page.
**Save the final page HTML** — `frontchannel_logout_supported` is true in discovery, so that
page may contain iframes to client logout endpoints carrying `sid` and `iss`, and the HTML is
the only place those appear.
(d) `&prompt=none&state=post-logout-check-2`. Expect `error=login_required`.
(e) Exactly (c) again with the now-stale hint, `state=logout-replay`.

Note every `Set-Cookie` on the logout responses: names, order, `Path`, `SameSite`, `Secure`,
`HttpOnly`, `Max-Age`. Clearing the session cookie is part of the contract, and M5 is Sessions;
a customer test that clears the broker session cookie by name will break against a StubID that
names it something else. The **values** never enter the repository (B8).

*Settles:* question 9's logged-in half in full — what endsession does with a real session and a
validating `id_token_hint`, whether `post_logout_redirect_uri` is honoured for this client,
whether `state` is echoed, whether the no-parameter form prompts or logs out silently, and
whether logout is idempotent. What front-channel logout actually emits, given discovery
advertises both front- and back-channel logout with session support while publishing no
`check_session_iframe`. And the negative that makes the rest trustworthy: `prompt=none`
returning `login_required` afterwards.

*This went wrong if:* `post_logout_redirect_uri` is not registered for the client and the
broker silently drops it, stranding you on its own page. That **is** the finding, and one
StubID must reproduce rather than improve on.

*Note:* (c) and (e) put a real id_token in a request URL, so without B2 they cannot land as
fixtures.

### Step 17. Timeout checkpoint

Return to browser 2. If nothing has happened by T+45, force the stale context to surface: type
any user ID and submit, or press the widget's primary button. A server-side expiry is usually
invisible until the next request, and this is what converts a silent timeout into a recordable
response. Export the HAR; if the failure renders as a broker page rather than a redirect, save
the page HTML too, because the RP will never see it.

*Settles:* question 10, and question 5 indirectly — the catalogue hedges on `mitid_no_ctx`
("In some circumstances, we can redirect the user back to the service"), so which of
`mitid_no_ctx`, `mitid_timeout`, `no_ctx` or `user_navigation_error_empty_state` appears, and
whether it arrives as a client redirect or as `/op/Error`, is exactly the hedge a recording
resolves.

*This went wrong if:* nothing has happened at all. Record the elapsed time and the last screen
state anyway. "Still alive after N minutes" is a usable lower bound and stops the next sitting
from re-running this blind.

### Step 18. Wrong authenticator, then abort (identity B, profile 4)

Droppable. Budget three minutes.

Fresh profile. Use **identity B**, never identity A — check the paper. Enter identity B's user
ID, then enter a deliberately wrong password three times. **After each rejection, look at the
URL bar and the RP log and confirm that no redirect leaves the broker.** That negative is a
finding in itself: StubID must not emit an error response for a failed authenticator attempt.
If a fourth attempt is offered, supply the correct password, then reject the approval in the
code-app simulator once and observe what the widget does. Then click abort. Export the HAR.

*Settles:* question 5's sharpest part — whether an abort after failed attempts produces
`mitid_core_client_error_user_abort` rather than `mitid_user_aborted`. The catalogue describes
it as exactly this scenario, so this recording plus step 2 is what lets StubID reproduce both;
step 2 alone would make the second code look unreachable. Also whether a rejected code-app
approval stays inside the widget or produces its own redirect, and whether MitID blocking an
identity surfaces as a distinct broker error code.

*This went wrong if:* you used identity A. Stop, and treat everything identity A does
afterwards as suspect. Otherwise: MitID may cap attempts before three, or show its own error
page and refuse to offer an abort — record the terminal screen and whatever the RP eventually
receives.

### Step 19. Identity C: no CPR, and the failed match (profile 3)

Fresh profile — identity A's session in profile 1 would reuse the wrong identity and silently
ruin both halves. Two authorizes, one MitID click-through.

**Authorize 1:** `scope=openid mitid userinfo_token transaction_token`, `prompt=login`, PKCE.
Complete the login with the app simulator. The RP takes the token exchange and one userinfo
call.

**Authorize 2**, same window, new state and nonce, **no `prompt`**:
`scope=openid mitid ssn userinfo_token transaction_token`. When the CPR prompt appears, type the
**second pre-chosen wrong CPR** from the redact list. Note verbatim what the screen says, and
run that transcription through the redact list before it reaches `meta.json`. If the screen
lets you retry, type the other pre-chosen wrong CPR once. Do not improvise further numbers.
Record whatever comes back at the redirect URI, including the full query string of an error
redirect, and the broker's page if the flow stops there instead of returning.

*Settles:* the false branch of `mitid.has_cpr`, which no other recording can reach — whether it
is the string `"false"` or the boolean `false`, and whether `mitid.date_of_birth` and
`mitid.age` survive when there is no CPR behind them. A **provably CPR-free** rich token and
userinfo response from authorize 1: the member set, member order and Content-Type of a
`userinfo_token`- and `transaction_token`-bearing response in a fixture that never contained a
personal number, which is the fallback that keeps the surface publishable if step 7's recording
ever has to be withheld or re-recorded, and which isolates which members are caused by scope
rather than by the presence of a CPR. Question 5 in part: the OAuth error value the broker pairs
with `mitid_cpr_match_failed` — only `access_denied` + `mitid_user_aborted` is documented, and
the catalogue in CAP-007 lists `mitid_cpr_match_failed` with no OAuth error value at all. Where
MitID's three-attempt rule is enforced — inside the broker's CPR screen with retries, or as a
single refusal that ends the flow — which is a behaviour StubID has to choose between. And what
the `ssn` scope does when the identity has no CPR to match at all.

*This went wrong if:* you ran it in profile 1. Also write "identity created without a CPR
number" into these fixtures' `meta.json` and do not generalise the error code to the ordinary
mismatch case: a CPR-less identity may fail the match differently.

### Step 20. Optional, if a protection control existed

Only if P1 found one. One mitid-only login on the protected identity plus a single userinfo
call, in yet another fresh profile. That settles the `NAVNE & ADRESSEBESKYTTET` value for real
rather than from documentation.

### Step 21. Tails and teardown

- If P4 found a **revocation** endpoint, revoke a token whose surfaces are all finished — step
  7's, not a live one — and record the response.
- **Userinfo with an expired access token.** Check step 6's `expires_in` first. If it is 3600
  seconds, a 90-minute sitting barely reaches it: try once, and if the token still answers,
  record the attempt and note that the expired case remains unrecorded rather than promising it.
- Final glance at browser 2 (step 1/17). Record the elapsed time whatever the state.
- Stop the harness. Run the staging pass (B7) over the complete set, then write the fixtures.
- Run all three guard tests on the whole tree.
- `git status`, then read the diff with `git add -p`. A human reads it. Nothing is pushed until
  after this, so the captured session identifiers and cookies are already dead by the time they
  are anywhere public.

**Drop list, if the sitting is running long, in this order:** step 20, step 18, step 14, step 3.
Dropping step 14 leaves `code_app_enchanced` unsettled; dropping step 18 leaves
`mitid_core_client_error_user_abort` unreachable. If the sitting overruns badly, the merge of
last resort is to fold step 9's `transaction_token` scope and `reference_text` into step 14's
authorize, which loses the transaction token at default assurance but keeps the reference-text
naming answer.

**Things deliberately not in this script**, so nobody adds them back mid-sitting: the
fragment-mode transcription (step 15); a third parked browser for a broker-page timeout, which
the design that proposed it already marked droppable; a second `prompt=none` variant with
`idp_values` omitted, which is a recorded null result; more than two wrong-CPR attempts; and
hunting the test tool for a protection control beyond the thirty seconds in P1.

---

## Part 4 — If something goes wrong

**The RP throws on a callback with no code.** Most failure recordings arrive that way. Fix the
handler and re-run; the failure steps cost no authentication, so re-running them is free.

**A code expires before exchange.** The login is spent. Redo it. This is why the callback
handler is smoke-tested against a free redirect in P4 rather than against a paid one.

**`prompt=login` did not actually re-authenticate.** `auth_time` unchanged from the previous
login means MitID kept its own session and the recording carries the previous authentication's
`amr` while looking correct. Do not file it as an assurance-level recording.

**`loa` came back as something other than what was requested.** `loa_value` is not validated at
authorize, so this means a misspelling. The recording is mislabelled, not useless — file it with
the `loa` it actually produced.

**No `transaction_token` in step 9's response.** The client has the scope granted but the
feature switched off. Abandon the transaction ride-alongs and report it; do not spend the
sitting hunting.

**The scope pile is refused at step 7.** Split it, record the refusal, and note the fallback in
meta. A rejected scope combination is a fact worth having.

**The CPR window expired.** Re-run the authorize with `prompt=login` and authenticate fresh. The
recording stays valid; only the session-reuse observation is lost.

**The CPR-match attempts run out early.** Expected — step 7 spends one. Every call is a fixture
wherever it lands. Do not re-run to make the numbering tidy.

**Identity B got confused with identity A.** Stop. Note the point at which it happened. Every
identity-A recording after that point is suspect, and it is cheaper to re-record them than to
publish a fixture describing the wrong subject.

**The bare `endsession` logged the user out silently.** That is a finding. Record it, then insert
one fresh login before continuing step 16.

**A ten-digit epoch trips the CPR guard.** A false positive costs a minute, which is the trade
the pattern was chosen for. Widen the boundary if it recurs, but do not panic-edit a fixture.

**A CPR, a token or a secret reached disk unredacted.** The fix is a re-record and a history
rewrite, never an edit. If a client secret is what leaked, rotate it. This is why the canary
dry-run (B16) exists.

---

## Part 5 — What this settles, and what it does not

| # | Question | Outcome |
|---|---|---|
| 1 | id_token member set and order | **Settled.** Header and payload member sets and order at three assurance levels, back-channel and front-channel, first login and silent re-issue. `nbf`, `sid`, `idp_environment`, `at_hash`, the type of `auth_time`, and whether `sid` and `auth_time` are stable across a session. |
| 2 | The `amr` wire form | **Mostly settled.** The claim name and the value form are settled outright by step 6. Multi-valued `amr` is settled if Low offers password-plus-code-display. `code_app_enchanced` is settled only if the simulator exposes the enhanced approval. **Open:** any documented value the test tool does not offer — `code_reader` and `u2f_token` are the likely gaps, and each would need an identity provisioned with that authenticator. |
| 3 | Userinfo value types | **Settled** for the `mitid`, `ssn` and `nemid.pid` claims, including whether everything really is a JSON string. **Likely open:** the `ssn.details_*` **success** branch, because a test-tool CPR need not exist in the pre-production register and `unable_to_lookup` is the expected answer — the failure shape is recorded, the success shape stays documentation-only. **Likely open:** `person_status` casing (lowercase versus PascalCase), for the same reason. **Open:** `name_address_protected` as a boolean versus the string `"false"`, unless a protected identity turns out to be creatable (P1's thirty-second check). |
| 4 | Transaction token claim names | **Settled** for everything a login produces: `identitytype` versus `identity_type`, the presence of `loa`/`aal`/`exp`/`aud`/`nbf`, `auth_time`'s type, `spec_ver`, `recipient_info`'s shape, `transaction_actions` in single- and multi-action form, `mitid.reference_text` versus `mitid.referencetext`, `mitid.psd2`'s type, the full member order, and which key signs it. **Open, and needs `signtext_api`, which the client is refused:** the transaction-**text** claims — the four-way spelling contradiction across `mitid.transaction_text_sha256`, `mitid.transactiontext`, `mitid.transaction_text`, `mitid.reference_text`'s signing-context sibling, and `mitid.transaction_text_type`. Also open: whether `signing_cert_ocsp_nonce` appears on a **signing** transaction token; step 9 can only show whether a login produces it. |
| 5 | OAuth `error` per broker error code | **Partly settled**, for the codes actually exercised: `mitid_user_aborted`, `login_required`, `mitid_uuid_hint_malformed`, `mitid_cpr_match_failed`, and — if reachable — `user_aborted`, `mitid_core_client_error_user_abort`, the navigation family, and whichever timeout code step 17 produces. **Open:** the rest of the catalogue, which is dozens of codes, most of them infrastructure or internal-error paths that cannot be provoked from a client at all. This question is never fully closable from the outside; the honest ledger entry is per-code. |
| 6 | The successful token response shape | **Settled.** Member set and order, `token_type` casing, `expires_in`, whether `scope` is echoed and in which order, and what `userinfo_token` and `transaction_token` add and where. **Permanently open, deliberately:** the refresh-grant response — `offline_access` is refused with `invalid_scope`, so no refresh token exists on this client and there is nothing to record. |
| 7 | `c_hash` | **Closed negatively, before the sitting.** Every `response_type` putting an id_token in the front channel is refused with `unauthorized_client` on the private client, on both published open code clients, and on the published implicit client. No client we can reach is entitled to hybrid, so `c_hash` is unrecordable against this broker and stays `FidelityProvenance.Assumed`, computed by the spec rule already in `HashClaims`. What **is** recorded: the byte-exact `form_post` envelope, and whether `s_hash` exists. |
| 8 | The userinfo success response | **Settled.** Content-Type, minified or indented, member order at minimal and fat scope, which of the three documented session-claim spellings is real, whether `idp_identity_id` appears and equals `mitid.uuid`, whether userinfo answers POST, and whether `Accept: application/jwt` changes anything. |
| 9 | End session | **Settled**, both halves: with and without a session, with and without a validating `id_token_hint`, whether `post_logout_redirect_uri` and `state` are honoured, whether the bare form prompts or logs out silently, whether logout is idempotent, what front-channel logout emits, and the cookie contract. |
| 10 | The undocumented flow timeout | **Partly settled at best.** The measurement runs for the length of the sitting, so the outcome is either an observed expiry with its wall-clock duration, or a lower bound of "still alive after N minutes". A bound is a usable fact and stops the next sitting re-running it blind, but it is not the number. **Open:** whether the broker's own request context and MitID's session expire on different clocks — the second parked flow that would have measured it was dropped as not worth a third browser. |

### Settled by this sitting but not on the original list

Whether the broker keeps an SSO session at all, which decides whether StubID implements one.
What an authorization-code replay does, and whether it revokes the tokens already issued. The
CPR-match API's wire shape and its three-attempt refusal. The `tokenverify` and
`nemidPidLookup` responses. The broker's login page and identity-provider chooser as committed
fixtures, which M3 needs and which nothing had captured. The cookie contract. A PAR round trip
with client authentication, and whether the advertised-but-unpublished revocation and
introspection endpoints exist. And, from the unattended pre-flight, that an interaction failure
**does** redirect back to the client with `state` and `session_state` and no `iss` — which
contradicts the rule the first pack established and would have made StubID wrong on a case a
stock client hits routinely.
