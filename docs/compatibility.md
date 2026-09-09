# What stays compatible

This page says what a suite built against StubID can rely on, what will move underneath it, and
what happens before it moves. It is the answer to the question a version number usually answers,
which this project's version deliberately does not.

Everything here targets `net10.0`, and only that. No package multi-targets, so a suite on an
earlier .NET cannot reference one at all.

## There is no 1.0

Not deferred — the wrong shape of question. StubID's version is a date, for
[the reason the first release notes give](releases/2026.09.1.md#the-version-is-a-date), and the
release workflow refuses a tag that is not the version the build declares. Nothing physically
stops somebody typing `1.0` into the version property, but doing so would be abandoning the
scheme rather than reaching a milestone in it, and the argument against that is the one linked
above.

What a 1.0 is normally *for* — telling somebody what they may depend on — is this page, and the
parts of it that can be checked are checked by the build rather than promised here.

## Four surfaces, four different promises

They are not one thing and they do not move together.

| Surface | What it is | The promise |
| --- | --- | --- |
| The .NET API you install | `StubId.Client`, `StubId.Testing`, `StubId.InProcess` | Stable. A member is deprecated for one release before it is removed, and the build enforces it. |
| StubID's own control API | The routes under `/_stubid/v1`, which `StubId.Client` covers member by member | Stable. Additions are expected; a rename keeps answering for one release, then stops. |
| The emulated broker | Everything under `/op` | **Deliberately not stable.** Getting closer to the broker changes bytes, and that is the point of the project rather than a regression in it. |
| Configuration and the container | `StubId:*` settings, `StubId__*` environment variables, ports 8080 and 8443, the `/keys` volume, the image tags | Additive. A removed setting or a moved port is a release note. |

`StubId.Abstractions` carries the same promise as the first row and appears in the API reference,
but nothing installs it directly — it arrives underneath `StubId.InProcess`, and a suite built on
`StubId.Client` or `StubId.Testing` never sees it.

`GET /_stubid/v1/routes` is not an index of the first two rows. It lists the *emulated* routes and
whether each one is answered, so it belongs to the third.

Two health routes — `GET /_stubid/health/live` and `GET /_stubid/health/ready` — sit outside
`/_stubid/v1` on purpose. The Testcontainers module waits on readiness before it hands you an
instance, so those two cannot move when the versioned group does.

## What is promised nothing

Said plainly, because the alternative is somebody finding out.

**The substrate packages.** `StubId.Server`, `StubId.Wire`, `StubId.Profiles.Abstractions` and —
by the definition above, since nothing installs it — `StubId.Abstractions` are published because
the packages you install depend on them, not because anything is meant to call them. Nearly all of
`StubId.Server`'s public surface is engine internals that are public only so the hosting packages
can reach them.

They are recorded in the surface baseline all the same, and the removal rule applies to them
mechanically: a member of any published package that disappears without a release of `[Obsolete]`
fails the build. That is a guard against carelessness, not a promise — a deliberate break here
takes an exemption entry and gets one, where the same break in the first row would not.

**Two escape hatches, by name.** The members themselves are on the stable surface; what they hand
you is not:

- `StubIdHost.Services` returns the instance's `IServiceProvider`, and through it any engine type
  at all. Its own remark is the honest description: *"The control API is the supported surface and
  the one the container shares; this is the escape hatch for reaching a collaborator directly,
  which no containerized suite can. Code written against it does not move to a container
  unchanged, which is the trade."*
- `StubIdClient.Http` is the raw transport, *"for anything this surface does not cover yet"*.

The property will keep existing. What you reach through it can move in any release.

**The admin pages.** `/_stubid/admin` is HTML for a person to look at: not an API, no version,
nothing to assert on. Note that it is not the authenticated half of anything — **neither the admin
pages nor the control API have any authentication, and there is no setting that adds one.** The
boundary is the port, which [the admin guide](guides/admin.md) explains at length.

**Anything below `FidelityTier.Exact`.** The tiers are the emulator's own statement of how hard it
is trying, and the rule is already written where the code can read it: *everything a client
library parses or validates is `Exact`, and anything below that is a promise StubID is not
making.* `Shape` means the differ asserts a pattern rather than a value: identifiers, timestamps,
key material. `OutOfContract` means StubID does not try to match it and tests must not assert on
it — the body of a 501 is the clearest example, where what you can rely on is the status and
nothing else.

## Three different things are called a version

They are told apart nowhere else, and confusing them is the easiest mistake to make here.

- **The build version** — `2026.09.3`. What is on nuget.org and what the image tag names. The
  package spells it `2026.9.3` because NuGet reads a version as numbers and drops the leading
  zero; the container tag keeps the padding because tags sort as text. Both resolve to the same
  release.
- **The profile version** — which *recording* of the broker is being served. It moves only when a
  release carries a new capture, so it lags the build and may never lead it. It has read
  `2026.09.1` since that release, because no sitting has been recorded since.
- **`StubIdSession.Version`** — a counter on a single login, so two reads of it can be told apart.
  It moves on every state transition, of which a decision is one: being decided, having the code
  collected, timing out undecided, and expiring after approval without collection each move it. It
  has nothing to do with either version above.

An instance will tell you which recording it is serving. `GET /_stubid/v1/fidelity` names it
ahead of the ledger, as `profile.broker` and `profile.version`, and `StubIdClient.ProfileAsync()`
reads it; an instance older than the release that added the key sends nothing, and the typed
client answers null rather than failing. Read it from the instance rather than inferring it from
the image tag, because the two move on different occasions and that difference is the whole point
of telling them apart.

One gap is left. The profile version is a hand-maintained literal: what keeps it honest is a test
pinning it and the rule that it may not lead the build, not a derivation from the recordings
themselves.

## What pinning a version pins

Every release note says to pin the version in anything that asserts on bytes. That is good advice,
and the mechanism is worth being precise about, because pinning the build version is sufficient
without being the thing that tracks bytes.

- Bytes reproduced **from a recording** are meant to track the **profile** version.
- Bytes StubID **composes** where no recording exists track the **build** version.

2026.09.3 is the demonstration of the second: `/op/connect/ciba` changed from 404 to 501 while the
profile stayed at 2026.09.1. So a build bump *can* move what the emulator answers under `/op`, and
once has — the release note for it is headed "The broker's bytes have moved once". 2026.09.2 is
the other case: a build bump that moved nothing under `/op` at all.

Pinning the three-part build version covers both, which is why the advice is stated that way.

## No fidelity correction has shipped yet

The argument for a dated version is that a fidelity correction — StubID emitting what the broker
really sends, where it previously did not — breaks anyone asserting on the bytes it corrects, and
that this is not a distinction a semantic minor can carry.

That argument is the reason for the version scheme. It is not yet a description of anything that
has happened: across three releases the recordings have not changed and the profile version has
not moved. The change to CIBA above is not one of these — it moved *away* from the broker, to a
declared out-of-contract answer, rather than closer to it.

The first real correction will break a suite that asserts on the bytes it touches, and the release
note will say so. Where one is most likely is not a guess: the fidelity ledger marks its own
weakest claims, and [the divergences](brokers/neb/divergences.md) list every entry that rests on
the broker's documentation rather than on a recording, together with the ones still awaiting a
capture that would settle them.

## Deprecation, and what one release of notice is worth

**The promise: a public member of the packages you install is deprecated for one release before it
is removed.**

The one worked example is the US-English conversion, and it is worth reading precisely rather than
as a success story. It renamed **eight** surfaces. Seven kept answering through 2026.09.2 and were
removed in 2026.09.3, which is the promise kept seven times. The eighth belongs here rather than
in a footnote: `BehaviourApi`, a public class in `StubId.Client`, was deleted outright with no
deprecation at all, because an alias for it would have meant unsealing the class that replaced it.
Of the seven that did get notice, three are members of the packages this promise covers, three are
in `StubId.Server`, which this page promises nothing about, and one is a control-API route.

None of that was enforced at the time. It is now, and the rule below would have failed the build
on `BehaviourApi`.

Two things that release did not buy, stated because a promise nobody can act on is not one:

- The `[Obsolete]` attributes carried no diagnostic identifier. A bare one raises the same warning
  as every other deprecation in a consumer's tree, so somebody building with warnings as errors —
  as this repository itself does — could suppress all of them or none. For them the release meant
  as a grace period was already a hard break. A test now fails on a deprecation that carries no
  identifier and no link, so the next one is suppressible on its own.
- The renamed control-API route carried no `Deprecation` or `Sunset` header, so a caller that was
  not a .NET compiler — Node, Spring, curl — got no signal at all before the route began answering
  404. That mechanism now exists, and the next deprecated route will carry it.

What a deprecated route sends, so that a caller can read it before there is one to read it on:

```http
Deprecation: @1688169599
Link: <https://github.com/benne/stubid/blob/master/docs/compatibility.md#...>; rel="deprecation"; type="text/html"
```

`Deprecation` is an item structured header field whose value is a Date ([RFC 9745]) — an `@` and
then seconds since the epoch — and it says when the route was deprecated, not when it goes. The
`Link` is where the reason is written.

`Sunset` ([RFC 8594], an HTTP-date, which is a different format on purpose) is sent only where a
removal has actually been scheduled, and usually it will not be. That field asks for a timestamp
in the future, and this project has no release calendar to draw one from; a date invented to fill
it in would be a promise about when a release happens, which is not a promise anything here can
keep. **So do not wait for a `Sunset` to act on a `Deprecation`.** One release of notice is what
is promised, and the deprecation header is the notice.

[RFC 9745]: https://www.rfc-editor.org/rfc/rfc9745.html
[RFC 8594]: https://www.rfc-editor.org/rfc/rfc8594.html

## How this is enforced

The .NET half is not a promise you have to take on trust. The public surface of every published
package is composed by reflection and committed as text, in two files: one for what the assemblies
say now, and one for what the last release published. A member that the last release published,
that is gone from the current surface, and that did not carry `[Obsolete]` when it shipped, fails
the build and is named. A break that is decided rather than accidental takes an entry recording the
reason, and a second test fails when such an entry stops describing a break that actually happened.

What is **not** enforced, so that you know where the edges are:

- **Route paths.** The baseline is composed from .NET members. A control-API path is a string
  inside a route registration, so a renamed or removed route is caught by whichever test happened
  to call it, not by anything systematic.
- **JSON field names, in general.** Some are pinned as literals by tests that read the server's
  actual bytes — the routes, fidelity and session-explanation payloads among them — but most are
  derived from property names by a naming policy and exercised through the typed client, which
  round-trips both sides of a rename without noticing.
- **The shipped baseline moving on its own.** Nothing rewrites it automatically. What forces it is
  a test asserting it names the version being built, so it is a release-time step somebody performs
  rather than a file that maintains itself.
- **Compatibility with the previous published package.** `tests/consume-nuget` compiles against
  nuget.org rather than against the tree, but it runs weekly and after a release rather than on a
  pull request, and it always compiles the current names — it proves the package resolves, not
  that the last one still would.

## One correction to an earlier statement

The dated release notes are history and are never edited, so a superseded promise is corrected
here instead.

The first release notes said the floating `2026.09` tag moves "to the next release **recorded** in
the same month". 2026.09.2 recorded nothing and the tag moved to it anyway, and the later notes
quietly dropped the word. The rule, stated correctly: **`2026.09` moves to the next release in the
same month, recorded or not, and only the three-part tag never moves.** Pin the three-part tag in
anything that asserts on bytes.
