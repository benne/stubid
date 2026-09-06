# Unreleased

Notes accumulate here as changes land, and this file is renamed to the version when a release
goes out. The dated files beside it are history and are never edited.

## The endpoint discovery advertises and this build skips now answers 501

`/op/connect/ciba` answered 404. The discovery document is served from a recording rather than
composed, so it advertises `backchannel_authentication_endpoint` the way the broker does — and
then nothing was behind it. A 404 says there is no such endpoint, seconds after discovery said
there is one, which sends whoever is reading the log after a routing fault that does not exist.

It now answers 501 on `GET` and `POST`, with a body naming the path and linking to the section
of the divergences that explains the decision:

```json
{
  "error": "not_implemented",
  "detail": "StubID does not emulate /op/connect/ciba.",
  "reason": "https://github.com/benne/stubid/blob/master/docs/brokers/neb/divergences.md#ciba"
}
```

Nothing about that shape is emulated and no test should assert on it beyond the status. The
README has described this behavior for some time and no route had ever carried it; a test now
holds the two together, and a second one fails if any endpoint discovery advertises stops being
either answered or declared missing.

`GET /_stubid/v1/routes` gained `emulated` beside each route, and `EmulatedRoute` gained a
matching property. It is not a positional member, so existing code that constructs or
deconstructs one still compiles, and a client pointed at an instance older than the field reads
every route as emulated rather than as unemulated — which is the answer that was true before any
route could be only advertised.

That second half took a fix rather than a declaration. The client serializes through a
source-generated context, for trim and AOT safety, and the generator writes an init-only member
as an unconditional object-initializer slot with no default recorded: an absent key landed as
`default(bool)` and overwrote the property's own initializer, inverting the answer for every
route at once against exactly the older instances the field describes. The wire shape now carries
it as nullable and the default is applied where the two are mapped.

## The fidelity ledger gained a divergence it should always have carried

`GET /_stubid/v1/fidelity` now reports that the `id_token_hint` at the end-session endpoint is
read rather than verified. The broker checks that hint; StubID accepts any three-part token whose
payload carries a `sid`, including one it never issued. That was written up in the divergences
from the start and annotated nowhere, so the one place a running instance could be asked about it
did not know. Nothing about what the emulator answers has changed.
