# Fixtures

Recordings of real broker exchanges. They are the authority for what StubID emits: where a
vendor document and a recording disagree, the recording wins.

## Layout

```
fixtures/neb/pp/CAP-nnn/request.json     what was sent, with credentials replaced by placeholders
fixtures/neb/pp/CAP-nnn/response.head    status line and response headers, in the order received
fixtures/neb/pp/CAP-nnn/response.raw     response body, exactly as served
fixtures/neb/pp/CAP-nnn/meta.json        what the recording settles, and what may vary between runs
fixtures/neb/pp/MANIFEST.json            sha256 of every file above
fixtures/neb/certificates.md             the JWKS certificates, decoded
```

Bodies are stored as served: no decompression, no reformatting, no reserialising. Member
order and whitespace are part of what is being pinned, and a JSON round-trip would quietly
destroy both.

## Numbering

- **CAP-001 to CAP-019** need no login and run unattended.

  ```
  export STUBID_NEB_PP_CODE_CLIENT_SECRET=...      # only needed to re-record
  dotnet run --project tools/StubId.CaptureHarness -- verify     # check for drift
  dotnet run --project tools/StubId.CaptureHarness -- capture    # re-record
  ```

  Use `verify` day to day. It re-requests everything and compares against what is committed,
  masking the values a fixture does not promise. `capture` overwrites the recordings, and
  since response headers carry a `Date` that shows up as a diff on every run, so reach for it
  only when something has genuinely changed.

  The credential is the broker's own published test-client secret, kept out of the repository
  rather than committed. Its documentation is where to get it. Cases needing it stop with a
  message rather than recording a confusing rejection.
- **CAP-020 to CAP-049** need a human to complete a login in MitID's test tool. They settle the
  things only a finished authentication reveals: the `amr` wire form, the id_token member
  set and order, the types of the userinfo values, and the transaction token's claim names.

  ```
  dotnet run --project tools/StubId.CaptureHarness -- session
  dotnet run --project tools/StubId.CaptureHarness -- session --only=CAP-031   # one step
  ```

  That hosts a relying party on `http://localhost:5099`. Work down the list it shows; each
  link starts one step, the browser goes to the broker, and the exchange is recorded. Nothing
  reaches disk until you visit `/finish`, because values born during the sitting appear in
  exchanges recorded before the response that first names them, so scrubbing can only be done
  once, over the whole set. `/finish` refuses if anything is still unaccounted for.

  A sitting after the first one wants a step or two rather than the list, and `--only` is how
  it says so: a step already recorded is one click away from being staged a second time and
  written beside the first.
- **CAP-040 onwards, in the unattended pack**, are probes added after the first sitting. They
  need no login, and they sit above the sitting's numbers rather than among them so that
  `capture` and the sitting can never be pointed at the same case.

  These land in `fixtures/neb/pp-session/` rather than beside the unattended pack: `capture`
  and `verify` iterate one catalogue, and a routine run would replay expired codes over the
  sitting's evidence.

  A later sitting records its own cases beside the existing ones and rewrites `MANIFEST.json`
  to cover them, keeping the date the pack already carries: that date says when the pack was
  made, and a sitting that adds one recording did not make the rest. From the second sitting on,
  each exchange carries its own `capturedAtUtc` in `meta.json`; the thirty from the first
  predate the field. `meta.json` is the only place an exchange's date is certain to appear —
  the broker's `Date` header is on twenty-one of the thirty-two responses, and a recorded
  callback has no response headers at all.

  Signed tokens are stored as a placeholder in the response body, with the decoded header and
  payload beside them. Scrubbing inside a token would invalidate its signature, and re-signing
  produces bytes nobody sent; the response's member order is what the body is for, and the
  token's own member order is what the halves are for.

  A step that sends a signed request object gets the same treatment on the request side, where
  the placeholder sits in the URL that `meta.json` records rather than in bytes as served. That
  object is the harness's own, signed with the client secret, so keeping it out of a fixture is
  a separate reason for the same rule.

## What this pack established

Four things worth naming, because each contradicts something that was believed beforehand.

**The transaction-signing key is published in the JWKS.** Decoding the certificate chain
shows the first signing key is `CN=NEB Transact PP`, issued by a Danish state test CA. The
broker's own documentation lists a thumbprint for it that no longer resolves, because the
certificate rotated in May 2026, which reads as though the key sits outside the JWKS. It
does not. StubID publishes its equivalent, so a client following the documented verification
path works against both.

**Values inside `idp_params` are not validated at the authorize endpoint.** A malformed
`uuid_hint` is accepted and only fails later in the MitID flow, even though an unknown
`idp_values` is rejected immediately. That is why the broker publishes a
`mitid_uuid_hint_malformed` error code at all. A stub built on reasonable assumptions would
have rejected it up front.

**Two endpoints on the same host challenge differently.** Userinfo answers
`Bearer realm="IdentityServer",error="invalid_token"`; CPR match answers a bare `Bearer`.

**The error catalogue is PascalCase on the wire and camelCase in the broker's own OpenAPI
document.** Generating a stub from the specification would be wrong on the first response.

## What the sittings established

`fixtures/neb/pp-session/` holds twelve cases and thirty-two exchanges from real MitID logins,
recorded by hand across two sittings — eleven cases on 2026-08-30, and CAP-031 on 2026-09-02.
Between them they settled things no documentation states:

- The id_token carries `nbf`, `sid`, `acr`, `idp_transaction_id`, `idtoken_type` and
  `subject_type`, four of which appear in no vendor claim table, and does **not** carry the
  documented `idp_environment`.
- The subject is scoped to the **organisation**, not the client: two clients of one service
  provider receive the same one. Deriving it per client gives an application that signs users
  in through two of its own clients two different people.
- `auth_time` is a number in the id_token and a string in the userinfo token, in the same
  response.
- `c_hash` and `at_hash` share one slot after `nonce`, and which appears depends on the
  channel rather than the flow.
- The `iss` authorization-response parameter is sent only when no id_token is returned.
- The documented `session_status` and `session_identifier` do not exist; the wire carries
  `session_is_active` and `session_expiry`.
- `auth_time` is a string in the transaction token too, which sends `amr` as a bare string
  where the other three tokens of the same response send an array.
- The transaction text comes back under both spellings at once, prefixed and unprefixed and
  underscored in both, so `mitid.transactiontext` is not a spelling this broker uses. Its
  `sha256` member is taken over the decoded text and encoded as base64 rather than hex.
- `transaction_actions` arrives as a bare string on the login-only recording and as an array
  on the two that did something else as well.
- `signing_cert_ocsp_nonce` appears on neither the login (CAP-022) nor the signing
  transaction (CAP-031), the only two recordings that could have shown it.
- The userinfo endpoint returns `mitid.transaction_text_type` and
  `mitid.transaction_text_sha256` without `mitid.transaction_text` — a digest without the text
  it is over — while a reference text comes back there whole.
- The OCSP response beside each transaction token says `good`, and says it about the
  transaction-signing certificate itself: its CertID's serial and issuer-name hash both
  reconcile against the `CN=NEB Transact PP` certificate in CAP-002. It is **not** fetched per
  request — `producedAt` is `2026-08-30T22:35:11Z`, `2026-08-30T22:40:11Z` and
  `2026-09-02T09:45:01Z`, the last of them three and a half minutes before the response that
  carried it. Two of the CA's responder instances answered across the two sittings, and each
  `nextUpdate` falls one second before the responder certificate expires, so an answer never
  outlives the key that signed it.

Written up in [../docs/brokers/neb/claims.md](../docs/brokers/neb/claims.md).

## Rules

Fixtures are scrubbed before they land. The published test-client secrets are replaced with
placeholders, which the harness substitutes back when it replays a case — a recorded
exchange containing something secret-shaped trips every scanner pointed at this repository,
and "that one is published on purpose" does not survive review.

The guard tests in `tests/StubId.Fixtures.Tests` fail the build on a credential, a signed
token, or anything shaped like a CPR number. They are not hypothetical: the first capture run
wrote a real secret into a fixture, because the scrubber ran after the form had been
percent-encoded and a plain string replace no longer matched.

## Rights

The recorded bytes are the broker's wire output: factual, functional protocol data, captured so
an independent implementation can reproduce the surface. StubID claims no copyright in the
recordings, and the repository's Apache-2.0 licence is not offered over them — they are the
facts the emulator answers to, not a work of this project. The selection of what to record, the
scrubbing, `meta.json`, and this document are the project's own and carry Apache-2.0 like
everything else here. Any database right in the collection as a whole (the EU sui generis right)
is the project's, and the project does not assert it.
