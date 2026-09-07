# Unreleased

Notes accumulate here as changes land, and this file is renamed to the version when a release
goes out. The dated files beside it are history and are never edited.

## The aliases the US-English conversion kept are gone

2026.09.2 renamed eight things on the public surface and kept seven of them answering as
`[Obsolete]` forwarding aliases. Its notes said in print that those go away in the release after
it, and the deprecation message on every one of them said the same. This is that release.

| Removed | Use |
| --- | --- |
| `StubIdClient.Behaviour` | `StubIdClient.Behavior` |
| `StubIdHost.Behaviour` | `StubIdHost.Behavior` |
| `StubIdContainer.Behaviour` | `StubIdContainer.Behavior` |
| `BrokerState.OrganisationOf` | `BrokerState.OrganizationOf` |
| `Client.Organisation` | `Client.Organization` |
| `PublicBaseUrl.TryNormalise` | `PublicBaseUrl.TryNormalize` |
| `POST /_stubid/v1/behaviours/enqueue` | `POST /_stubid/v1/behaviors/enqueue` |

They existed so that a suite written against 2026.09.1 would compile and keep answering across
exactly one release. That release has shipped. Code naming one of the six members now fails to
compile rather than warning, and the compiler names every site; the seventh, the route, answers
404 where it accepted an enqueue.

Nothing else changed with them. The new names have been there since 2026.09.2 and are untouched,
so a suite that took the warning seriously when it appeared has nothing to do here.

## The documentation is published, at stubid.dev

Every release now deploys the site as well, from the tag rather than from master, so what it
describes is the version on nuget.org rather than whatever master happens to be. Between releases
the two diverge, and that is the point: a reader who installed a package should not be reading
about a route it does not have.

A pull request still builds the site and deploys nothing, which is the rehearsal — the same reason
the release workflow's dry run exists. The deploy sits on the same dependencies as the GitHub
release, so the site goes live beside it and ahead of the NuGet push, which is the order this
repository already accepts.

`README.md` stops saying the documentation site is missing and names it. That README ships as the
package page for four of the seven packages, so the link is a real one now rather than a promise.

## The documentation builds as a site

`docfx.json` builds everything in `docs/` into a site, with an API reference generated from the
doc comments of the four packages a reader installs. It is pinned as a local tool, so nothing new
had to be installed in CI beyond what a .NET repository already has.

Nothing publishes yet — that is the next change. What this one buys is that every pull request
builds the site with `--warningsAsErrors`, which makes docfx the first thing in this repository
that has ever resolved a link. It checks both files and anchors, and it found three links that
reached out of `docs/` into `samples/` and `.github/`: those resolve on a repository page and
resolve nowhere on a site of their own, and they were already rewritten as full URLs.

`docs/index.md` is new and is deliberately not the README. The README's links are absolute GitHub
URLs because four packages ship it as their NuGet page, so rendering it as the site's front page
would send every visitor straight back to GitHub.

`docs/brokers/neb/index.md` is new too, and it fixes something older than the site: the broker's
errors and request parameters had nothing linking to them anywhere, and were reachable only by
someone who already knew the path. A test now fails if a page under `docs/` is in no table of
contents, or if the navigation names a page that is not there.

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
