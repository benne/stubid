# Roadmap

Status of the first emulated surface, Signaturgruppen Broker ("Nets eID Broker").

| | Milestone | Status |
| --- | --- | --- |
| M0 | Repository, license, CI, day-zero probes | done |
| M1 | Recordings that need no login (discovery, JWKS, error shapes) | done |
| M1.5 | The manual recording pass (one login, signing, error paths) | done |
| M2 | Token writer, keys, JWKS | done |
| M3 | First working login: a stock ASP.NET Core app signs in — **v0.1** | done |
| M4 | Broker profile seam, routing, fidelity ledger | routes and ledger done; claims, errors and keys stay in the engine until a second broker is recorded, [on purpose](explanation/profile-seam.md) |
| M5 | Sessions, approval resolution, citizens | done |
| M6 | Full request surface, error fidelity, logout | done |
| M7 | Control API, test modules, TLS trust, quickstarts | done |
| M8 | Admin interface | done |
| M9 | Transaction signing | done |
| M10 | Release engineering | done |
| M11 | Documentation site and generated broker reference | done |
| M12 | v1.0 | not started |

Every instance serves an admin interface since 2026-09-05. It shows logins arriving and decides
them, manages the people they sign in as and the decisions queued for them, and reads what the build
emulates out of the routes it loaded and the fidelity attributes on the code that answers - a page
generated rather than maintained beside the thing it describes. Everything it shows, a test can
fetch as JSON, so the pages never became the only way to see something; what it never shows is the
value of a code or a token. It is served on whichever listeners an instance has and has no
authentication, which is [written down](guides/admin.md) rather than left to be discovered.

There are samples to run since 2026-09-04, which is what the word "quickstarts" in M7 turned out
to mean: it had never been given content anywhere. `samples/aspnetcore` is a stock ASP.NET Core
application that signs in against a container, and it is the first application in this repository a
reader can start rather than read - every other one is a test server built inside a test. The Node
sign-in moved out of `tests/` beside it, and [one guide](guides/signing-in.md) routes a reader to
whichever of the four client stacks is theirs. Spring and the browser matrix keep the checks they
already had; neither needed a sample, and the guide says which of the two signs in and which only
resolves metadata.

There is a documentation site since 2026-09-07, at [stubid.dev](https://stubid.dev/). It carries
the guides, the broker reference, the research notes and an API reference generated from the doc
comments of the four packages a reader installs - and those four are now held to having them, so an
undocumented public member fails their build. It is published from a tag rather than from master,
so it describes the version on nuget.org rather than whatever master happens to be.

The "generated broker reference" half turned out to mean something narrower than it sounds, and
better. The reasoning in the divergences is not derivable from an annotation and stays hand-written;
what is written out from the fidelity ledger is the index of which divergences exist and what each
unsettled one is waiting for. Around that sit the guards this milestone was really about: every
anchor a reason points at has to resolve, every capture the documentation cites has to be a
recording that is there, the claims tables have to list members in the order the recording sends
them, and every endpoint the discovery document advertises has to be answered or declared
unemulated. That last one closed a contradiction the repository had been carrying - the README
promised 501 for what is not emulated while the divergences said the CIBA endpoint 404s, and the
divergences were right.

Released since 2026-09-04. `StubId.Testing`, `StubId.InProcess` and `StubId.Client` are on
nuget.org, and `ghcr.io/benne/stubid` is on GHCR for linux/amd64 and linux/arm64, carrying a
provenance attestation. Versions are dates rather than semantic versions, for the reason
[the release notes](releases/2026.09.1.md) give. Before this the guides told readers to pull an
image that did not exist and named packages with no way to obtain them.

A parked login can be resumed since 2026-09-03: approving on StubID's own page returns the
browser to the client with a code, and so does a decision made through the control API once the
browser comes back for it. Until then a parked login was a dead end, and every guide told readers
to queue an outcome rather than click through.

Deferred past 1.0 on purpose: hosted multi-tenant service and its accounts, CIBA, PAdES
document wrapping, the Idura profile, and NemLog-in / OIOSAML.

The transaction token's text claims are recorded. A second sitting on 2026-09-02 sent a signed
request carrying a transaction text and took CAP-031, which settles the last row a login could
close; what came back is in [the claims reference](brokers/neb/claims.md). Still unseen: the
address members under `ssn.details_*`, because the test identity has no register entry behind
it.

Four behaviors are implemented from documentation rather than from a recording. Three of them
need a completed login to reach: end session honoring a post-logout redirect when it is given a
valid `id_token_hint`, the CPR-match refusal after three attempts, and `prompt=none` answering
`login_required`. The fourth is the type of `cprNumberMatch`, which no capture reached a
successful match to settle, so it is the pre-production swagger's boolean — worth doubting, since
every other value on that endpoint is a string. Each is marked as such in the fidelity ledger and
listed in [the divergences](brokers/neb/divergences.md).
