# Unreleased

Notes accumulate here as changes land, and this file is renamed to the version when a release
goes out. The dated files beside it are history and are never edited.

## The four packages a reader installs document their whole public surface

`StubId.Client`, `StubId.Testing`, `StubId.InProcess` and `StubId.Abstractions` had 54 public
members with no documentation between them, including all five entry points on `StubIdClient` —
`Citizens`, `Sessions`, `Behavior`, `Time` and `Runtime`, which are the first things anyone using
the library touches. The obsolete British-spelling alias two lines from `Behavior` was documented;
those five were not.

They are now, and CS1591 is an error in those four projects rather than suppressed, so the gap
cannot come back. It stays suppressed everywhere else: `StubId.Server`'s public surface is nearly
all engine internals that are public only so `StubId.InProcess` can reach them.

Two things the writing turned up, both fixed:

`SessionState.Expired` said nobody decided the login in time. It also covers a login that *was*
approved and whose code was never collected inside a second window of the same length, which is a
different thing to debug. The server's own enum carried the same sentence and now says both.

The clock is readable on every instance and movable only where the instance was started with a
controllable one. Three properties described it as though the whole thing depended on that flag.

What the gate does not cover, said plainly rather than implied: CS1591 does not fire on a record's
positional parameters. The client's record types — `StubIdCitizen` and its neighbors — still carry
about a hundred positional properties with no `<param>` tag, and nothing here changes that or
stops the next one shipping the same way. They are a documentation gap the compiler cannot see,
and the published API reference is where they will show as blank.

## The divergences carry the ledger itself, written out rather than typed up

`docs/brokers/neb/divergences.md` now ends with the whole fidelity ledger as a table, and the
section on what no recording settled carries a second table saying what each of those is waiting
for. Both are generated from the annotations, in the same order the admin pages use: what is not
reproduced at all, then what diverges on purpose, then what rests on documentation, and only then
what a recording confirmed.

The prose around them is unchanged and stays hand-written. What a divergence costs somebody is not
derivable from an annotation; which divergences exist is, and that was the half that could drift.

There is no tool to remember to run. A test composes the tables and compares them to what is
committed, so a stale one is a failing build, and `STUBID_UPDATE_DOCS=1` makes the same test
rewrite them.

`AwaitingCapture` now appears somewhere a person can read it. It carries the longest prose in the
ledger — which recording would settle an assumed member's slot — and until now it was served in
JSON and shown nowhere. It is a fifth column on the admin page's fidelity table as well.

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
