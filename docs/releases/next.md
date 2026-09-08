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
