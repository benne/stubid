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
- **CAP-020 onwards** need a human to complete a login in MitID's test tool. They settle the
  things only a finished authentication reveals: the `amr` wire form, the id_token member
  set and order, the types of the userinfo values, and the transaction token's claim names.

  ```
  dotnet run --project tools/StubId.CaptureHarness -- session
  ```

  That hosts a relying party on `http://localhost:5099`. Work down the list it shows; each
  link starts one step, the browser goes to the broker, and the exchange is recorded. Nothing
  reaches disk until you visit `/finish`, because values born during the sitting appear in
  exchanges recorded before the response that first names them, so scrubbing can only be done
  once, over the whole set. `/finish` refuses if anything is still unaccounted for.

  These land in `fixtures/neb/pp-session/` rather than beside the unattended pack: `capture`
  and `verify` iterate one catalogue, and a routine run would replay expired codes over the
  sitting's evidence.

  Signed tokens are stored as a placeholder in the response body, with the decoded header and
  payload beside them. Scrubbing inside a token would invalidate its signature, and re-signing
  produces bytes the broker never sent; the response's member order is what the body is for,
  and the token's own member order is what the halves are for.

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

## Rules

Fixtures are scrubbed before they land. The published test-client secrets are replaced with
placeholders, which the harness substitutes back when it replays a case — a recorded
exchange containing something secret-shaped trips every scanner pointed at this repository,
and "that one is published on purpose" does not survive review.

The guard tests in `tests/StubId.Fixtures.Tests` fail the build on a credential, a signed
token, or anything shaped like a CPR number. They are not hypothetical: the first capture run
wrote a real secret into a fixture, because the scrubber ran after the form had been
percent-encoded and a plain string replace no longer matched.
