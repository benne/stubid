# Unreleased

Notes accumulate here as changes land, and this file is renamed to the version when a release
goes out. The dated files beside it are history and are never edited.

## The site has a mark of its own

`stubid.dev` shipped with docfx's stock icon in the tab and the header. It now carries one drawn
for the project: an ID card as a dashed outline around a person, which is the placeholder
convention doing the work the name does — identity-shaped, and openly not the real thing. Nothing
about it approximates any broker's logo, which the non-affiliation notice in `README.md` makes a
requirement rather than a preference.

The tab icon is an `.ico` carrying 16, 32 and 48 pixel cuts rather than a scaled vector, because
a dashed outline is exactly the shape a browser downscales badly; the header takes the vector.
Both sit at the site root, where they displace the two files the template would otherwise put
there, so a browser that probes `/favicon.ico` without reading the tag gets this one too.

## The research notes are no longer on the site

`stubid.dev` carried four research notes — the day-zero probes, the observed tokens, the signed
requests and the transaction screens. They are the measurements the broker reference was built
from: working notes, dated and kept as written, rather than documentation for somebody using the
emulator. The site now stops at what a reader needs, and the notes stay in the repository where
they are still cited by name.

Four pages in the broker reference link to them and now do so by full URL, which is what the
guides already do for the two files under `samples/`. Anything bookmarked under
`stubid.dev/research/` moves to `github.com/benne/stubid/blob/master/docs/research/`.

The files themselves have not moved and will not: `docs/research/signed-requests.md` is the
evidence behind two entries in the fidelity ledger, and `GET /_stubid/v1/fidelity` serves that
path as a string. Relocating it would change an answer on the wire to make a navigation menu
shorter.

The runbook for taking a recording stays on the site, under Explanation rather than beside the
notes it is not one of.

## The public surface is written down, and the build holds it

Every package this repository publishes now has its public API recorded as text, and a test that
fails when the assemblies and the file disagree. Until now nothing noticed a removed public
member: the six spelling aliases deleted in 2026.09.3 went without a single test failing, because
the only code that had used them was deleted in the same commit.

There are two files rather than one. One is the surface as it stands, rewritten whenever it
changes. The other is the surface the last release published, rewritten only when a release is
cut. Comparing them is what turns "deprecated for one release before removal" from a sentence
into a rule: a member that the last release published, that is gone now, and that did not carry
`[Obsolete]` when it shipped, fails the build and is named.

There is a door for a break that is decided rather than accidental — it takes an entry carrying
its reason, and a second test fails when an entry stops describing a break that really happened,
so an exemption cannot outlive what it excused. The door is empty today.

The first member added since found a hole in how the surface was read. Reference nullability was
recorded only at the outermost level, so `Task<StubIdCitizen?>` was written down as
`Task<StubIdCitizen>` — and that is the shape most of the client's contract takes, since the
methods that answer "not there" with null are the ones that return a task of something nullable.
Eight members across four packages were recorded as promising a value they can return null for.
The surface is now read at every depth, and both files are rewritten: the record of what the last
release published names the same members as before, spelled correctly. Nothing about those
packages changed — what changed is that dropping one of those annotations is now a diff somebody
sees.

None of this changes what any package does. What it changes is that the next time this project
breaks something, it will be on purpose.

## There is a compatibility statement, and no 1.0

The roadmap promised a 1.0 from the scaffolding commit onward, and nobody had asked what it would
mean. It turns out to be the wrong shape of question: this project's version is a date, argued for
in print in the first release notes, and the release workflow refuses a tag that is not the version
the build declares. What a 1.0 is normally *for* — telling somebody what they may depend on — is
now [a page of its own](https://stubid.dev/compatibility.html), and the number is gone from the
roadmap and the README rather than deferred again.

It names four surfaces that do not move together: the .NET API of the packages you install, which
is stable and enforced by the surface baseline; the control API under `/_stubid/v1`, which is
stable; the emulated broker under `/op`, which is deliberately not, because getting closer to the
broker changes bytes and that is the point of the project rather than a regression in it; and the
configuration and container contract. It is equally explicit about what carries no promise — the
substrate packages, the admin pages, and the two escape hatches, `StubIdHost.Services` and
`StubIdClient.Http`, named outright, because a boundary that excludes `StubId.Server` while a
documented property hands you every type in it is not a boundary.

Three things it says that are worth repeating here.

**No fidelity correction has shipped yet.** The argument the dated version scheme rests on is the
reason for the scheme, not a description of anything that has happened: across three releases the
recordings have not moved.

**The one deprecation this project has performed is a counterexample as well as an example.** The
US-English conversion renamed eight surfaces; seven got a release of notice and the eighth,
`BehaviourApi`, was deleted outright from `StubId.Client` with none. The rule added alongside this
page would have failed the build on it.

**A bare `[Obsolete]` was never a grace period for everyone.** It raises the same diagnostic as
every other deprecation in a consumer's tree, so anyone building with warnings as errors — as this
repository does — could suppress all of them or none. A test now fails on a deprecation carrying no
identifier and no link, so the next one is suppressible on its own.

The page also carries a correction the dated notes cannot: the first release said the floating
`2026.09` tag moves to the next release *recorded* in the same month. 2026.09.2 recorded nothing
and the tag moved anyway. It moves to the next release in the same month, recorded or not.

## StubId.Server stops advertising itself

Every document in this repository calls `StubId.Server` substrate — published because the packages
you install depend on it, not because anything is meant to call it. Its project file disagreed: it
carried `PackageTags`, which is what makes a package findable by searching nuget.org, and shipped
the README as its package page. Both predate the first release, so
`docs/releases/2026.09.1.md`'s claim that the substrate packages "carry no package tags and no
readme" was true of three of the four and never of this one.

It now carries neither, matching `StubId.Abstractions`, `StubId.Wire` and
`StubId.Profiles.Abstractions`, whose project file is where the rule was written down in the first
place: *"a dependency a reader finds by searching the tags is a dependency they will reference
directly."*

**What changes for you: nothing you can call.** The package still publishes, still carries the
same version and the same assembly, and anything referencing it keeps working. What changes is
that it stops appearing in a tag search on nuget.org, and its listing page no longer shows the
project README — it shows its own description, which now says outright that it is substrate and
names the two packages a suite should reference instead.

A test now reads both halves rather than trusting either: the packages carrying tags, from the
project files, and the packages a guide tells a reader to install, from the `dotnet add package`
lines. They have to be the same set.

## An instance will say which recording it is serving

Every release note tells a suite asserting on the broker's bytes to pin the version. There are two
of those, and the page on compatibility spends a section separating them: the build version, which
the image tag and the package name, and the profile version, which says which capture of the
broker is being reproduced. Until now only one of them could be read off a running instance, and
it was the other one.

`GET /_stubid/v1/fidelity` now names the recording ahead of the ledger it already served:

```json
{ "profile": { "broker": "neb", "version": "2026.09.1" }, "entries": [ ... ] }
```

`StubIdClient.ProfileAsync()` reads it. Against an instance older than this release the key is not
sent and the method answers null rather than throwing, because the package and the image are
versioned apart and a suite can hold a client newer than the container it drives.

Nothing else about the route moved: the `entries` array is unchanged, and a caller reading only
that sees no difference.

## A route that is going away will say so on the wire

The compatibility statement promises that a control route which is renamed keeps answering for one
release before it stops. A .NET caller collects that promise as `[Obsolete]` and a build warning.
Everyone else collected nothing: when `/behaviours/enqueue` was retired, a caller written in
anything but .NET got no in-band signal at all, and the release meant as their grace period was a
release in which nothing told them.

A deprecated route now carries the two header fields the standards define for it, plus a link to
where the decision is written:

```http
Deprecation: @1688169599
Link: <https://github.com/benne/stubid/blob/master/docs/compatibility.md#...>; rel="deprecation"; type="text/html"
```

`Sunset` is sent only where a removal has actually been scheduled. RFC 8594 asks for a timestamp
in the future and this project has no release calendar to draw one from, so a date put there to
fill the field in would be a promise about when a release happens rather than a fact. Act on the
`Deprecation`; do not wait for a `Sunset`.

**Nothing is deprecated in this release, which is the point of building it now.** A mechanism
first exercised on the day it is needed is one whose header formats nobody has checked, and these
two are easy to get the wrong way round — one is seconds since the epoch, the other an HTTP-date.
The notice is attached to the endpoint as metadata as well as written to the response, so the
build can see it too, and a deprecation whose link points at a page that has since been renamed
fails a test rather than reaching a caller.
