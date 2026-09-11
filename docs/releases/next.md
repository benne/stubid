# Unreleased

Notes accumulate here as changes land, and this file is renamed to the version when a release
goes out. The dated files beside it are history and are never edited.

## A broker says where its own pages are, and says nothing where nobody has looked

Two literals decided what an answer was called — the error page's path and the login page's — and
both were spelled out in the classifier and in the rehearsal, which is two places to edit and one
of them to forget. They belong to the broker now.

The second broker declares no login path at all, and that is the point rather than an omission.
Both run IdentityServer, so guessing its login path from the first is tempting and is the
dangerous kind of nearly-right: its MitID journey is known to leave the tenant host entirely for a
shim on the older platform. Nothing classifies as a login redirect while the list is empty, and a
run that meets one prints the location it actually reached, which is the observation that fills the
list in.

`BareJson` described a refusal that says nothing beyond its code, which is a finding about the
first broker rather than a fact about OAuth. A refusal that carries a description or an `error_uri`
— which is what the second broker is expected to send — now has a name of its own instead of
falling through to `Unclassified`, where it would read as a surprise rather than as the answer.

One recording was already in that position. `CAP-046` records a pushed authorization request
refused with `{"error":"invalid_request_object","error_description":"Invalid JWT request"}`, and it
was `Unclassified` only because there was no name for that shape.

Two recordings were re-made to bring the pack back into agreement with the catalog, and the drift
they carried is worth naming because nothing would have found it. A `meta.json` records the case's
expectation as it stood when the case was last recorded, so editing a case without re-recording it
leaves a committed file claiming something the recording beside it contradicts. `CAP-043`'s meta
said the broker sent a request with no scope to its login page; the `response.head` beside it
records a redirect to the error page, which is what the catalog has said for some time. Its
`settles` text was a version behind as well, and `CAP-046`'s description still had a spelling the
repository converted away from. Both bodies are byte for byte what they were — only the volatile
headers and the stale claims moved.

A new theory closes it: every recorded response must classify as the case that recorded it expects.
It is asserted against the catalog rather than against the committed meta, because the case is the
live claim and the meta is a copy of it.

## Two crashes that were waiting for a first recording of a second broker

`CertificateReport` read `x5c` without asking whether the key set published one, and a key set
that does not would have crashed a first capture at its second case, leaving a pack half written.
`TokenFixtures.Verify` caught three exception types and not the one an elliptic key throws when it
is read for an RSA modulus — so instead of answering false, it would have surfaced during a
sitting.

## A request object is signed by a signer, not by a secret

The harness signed HS256 over the client secret because that is what the first broker accepts,
measured rather than assumed. The second advertises nine request-object algorithms and not one of
them is symmetric, so the same code could not have reached its transaction consent flow at all —
which is the one MitID feature in scope beyond login.

`RequestObject.Build` now takes a `RequestSigner`. `ClientSecret` is what it always did and the
first broker keeps it: its recordings were made with it, and re-signing them differently would
contradict the segment lengths a sitting's fixtures record. `PrivateKey` reads a PEM and signs
RS256 — or ES256 with the concatenated signature a JWS is read in, rather than the DER form every
default overload writes, which verifies nowhere and presents as a bad key.

The header gains a `kid` where there is one. That half has never been exercised: the extractor has
always read a key identifier back out of a header and has never once found one, because nothing
the harness signed had a registered key behind it. A broker holding one key for a client may
resolve it without being told, so `check` says that is a guess rather than assuming either way.

## `check` answers the question a sitting would answer expensively

It now fetches the discovery document and says whether the broker advertises the algorithm the
harness would sign with. That is the check that would have caught HS256 before a sitting instead
of during one, and it keeps catching it the day a broker changes the list. It compares the
document's `issuer` against the configured authority in the same fetch — a cheap way to notice a
wrong tenant on a broker whose tenant is its hostname, where a wrong subdomain produces a document
that parses perfectly and belongs to somebody else.

Where signing needs a key, it reports what the key is and prints the public half with its
thumbprint. Registering a key is a copy between two windows, and the mistake that invites — a key
registered that is not the key being signed with — earns a refusal indistinguishable from a
malformed object.

A sixth guard joins the five over the working tree: no private key reaches the repository. It is a
new class of secret, it matches none of the shapes the others know, and `*.pem` is gitignored
beside it.

## A step names the client it records with

`ClientProfile` was an enum of seven values whose every arm resolved to one broker's setting
names. It is now a roster of registrations — what the client is called, what configuration it is
in, and which settings hold its identifier and secret — and a step names one rather than selecting
from an enum that could only ever describe the first broker.

Naming one is required, with no default. On the second broker a claim is a property of the client
as much as of the broker: its dashboard aliases claims per client, and one setting decides whether
a claim reaches the identity token at all. A step that did not say which client it used would
record something nobody could read back.

`check` reads the roster too. Asking which steps a missing setting blocks used to mean asking
whether the setting's name contained `SSO_A`, which was the roster written a second time in string
matching. That is now a lookup, and the answers are pinned against what the enum gave.

Two things the compiler used to check are checked by tests instead, and one of them the enum never
checked at all: that every registration still reads the settings its enum arm read, and that every
step names a client belonging to the broker whose sitting it is. A third is new — every setting a
registration names must be one the scrubber replaces, because a client identifier comes back
echoed in a login redirect, and that is how a private one reached a fixture the first time round.

## The capture harness asks which broker it is recording

Every command takes `--broker=`, and there is no default. The fixture root, the sitting's pack,
the authority, the key set and the certificate report all come from that one word, and a command
without it refuses and names the two it knows.

There was a reason to make it explicit rather than pick a default. The sitting's directory used to
be found by taking the unattended pack, walking three parent directories **by depth alone without
checking a single segment name**, and re-appending a literal path. Any pack at the same depth was
therefore written into the first broker's session pack, whose manifest was then rehashed over it —
no error, no warning. That arithmetic is gone; both packs now derive from the repository root and
the broker, found the way `capture.local.json` is found.

`check` reports the broker it is checking, resolves that broker's authority before anything else,
and skips what needs the live host rather than throwing when it cannot. Where a broker's authority
names a value the local settings hold, an unset value is reported instead of crashing the report
whose job is to report it.

## A recording is checked against the request that produced it

A recording is only evidence of what a broker does if the request that produced it is still the
request the catalog makes. Nothing checked that, so the catalog and the pack could drift apart in
a refactor while every assertion downstream went on passing against bytes obtained by asking a
different question.

`RecordedRequestTests` compares every case's method, URL, form body and header names against the
`request.json` the pack recorded, and asserts that the pack holds a recording for every case and
nothing else. It reads two files and needs no network and no credential, which is what makes it a
gate rather than something somebody remembers to run.

## Nothing committed can name the account a recording came from

The second broker's tenant is its hostname. The first broker's pre-production host is public and
shared, so no recording of it could say whose it was; a Signicat sandbox puts the account's own
subdomain in the issuer, in every absolute URL inside the discovery document, and in `iss` in
every token. Two guards now cover ground the four already there could not.

The first needs no configuration. A tenant is a label under a known suffix, and a suffix is a
shape, so it holds on a machine that has never recorded anything — including every machine CI
runs on, which is where a document somebody typed by hand would otherwise arrive unread. Both
ways of writing the URL still pass without an exemption: `{{SIGNICAT_DOMAIN}}`, which is what the
scrubber substitutes, and `<subdomain>`, which is what the research notes use.

The second finds what has no shape at all — a client identifier, a key identifier, an
organization's name, an account word in a sentence — by searching for the values the local
configuration actually holds. On a fresh checkout it has nothing to look for, and that is the
right population rather than a gap: those values are only knowable to the people who can commit
them. It reads the working tree rather than the index, so an untracked scratch file is scanned
too, and a failure names the setting instead of the value, because a build log is not a place to
put the thing the check exists to keep out of one file.

`capture.local.json` gains the Signicat vocabulary alongside it: the tenant domain, the client id
and secret, and the identifier of the signing key a request object names in its header. The
domain is the one to fill in before recording rather than after — a case template that names
`{{SIGNICAT_DOMAIN}}` refuses to be sent at all until the setting resolves, which is what keeps a
first recording from writing the account's hostname into every fixture it makes.

All six read a wider set of files than they did. The list was what a recording is written in
plus the repository's own source and documentation; it now also takes what a sitting leaves
beside a recording — an exported HAR, a scratch log, a saved response, a note in a text file.
Those are the ones nobody thinks to look at afterwards, because none of them was going to be
committed on purpose.

`check` now reports those settings beside the first broker's, and reads them from the scrubber
rather than from a list of its own. It used to keep a second copy of the nine names, which is the
shape of drift where a tenth gets scrubbed and never checked. It also says which values are too
short to search a repository for, since those are replaced in a recording but cannot be looked
for in a document.

## The broker an instance serves is now chosen, not assumed

`StubId__Profile` picks it, and `neb` is what it is when nothing says otherwise — so a suite
written before this setting existed keeps working without being told about it. `StubIdBuilder` and
`StubIdHostBuilder` both take `WithProfile`, and a name this build does not serve is refused where
you can still see it rather than starting the wrong broker quietly.

There is one broker to choose today, which is the point: the setting exists so that the second one
is a configuration change rather than a fork. Choosing is also now the only way `Authority` can be
right. It used to return the address with `op` appended, and `op` is Signaturgruppen's own path
segment rather than anything of StubID's.

## A profile declares where its surface sits

`IBrokerProfile` gains a `TenantRoot`: the path a broker's surface begins under, how that path is
compared, and whether a trailing slash below it is refused. Three things read it that a route table
could not tell them — the gate that runs before routing, the issuer, and the authority the hosting
packages hand a caller.

Before this, `/op` was written into the engine in five places, and the seam that was supposed to
hold a broker's personality held only its route table. The consequence was not theoretical: the
Idura profile has been in the repository since the seam was cut, declared and served, and loading
it into the real application would have answered 404 for every one of its routes, because the path
gate was constructed with a literal. Its own tests never caught that — they build a bare host with
routing on it and never call `AddStubId` or `UseStubId` at all.

They do now. `HostedProfileTests` runs Idura through the composition an instance actually uses, and
checks the three things that separate a gate from a hard-coded prefix: a profile at the host root
answers where it said it would, StubID's own `/_stubid` surface is still reachable past a tenant
whose first path segment is dynamic, and the strictness is the profile's — Idura tolerates a
trailing slash where Signaturgruppen refuses one. There is a negative control beside them, because
a stub looser than the broker passes a client the real thing would fail, and that failure arrives
in production rather than in the suite.

## The issuer is composed per request

`iss` used to be built in the handler from a literal `/op`, and from a public base URL read per
request. It is now built from that same live address plus the loaded profile's root. The route
loader still hands a profile an issuer, but that value is a snapshot taken when the routes loaded,
and a container does not learn its own mapped host port until Docker has started it — so a handler
reading the snapshot would have emitted a stale issuer in every token of every container login.

## Two smaller things

`GET /_stubid/v1/fidelity` reports `profile.root` beside the broker and the version, which is how
the Testcontainers module knows what authority to hand you without keeping a table of its own.

The refusal you get for a public base URL with a path in it no longer names `/op`. It names the
path you actually sent, which is the same help for whichever broker is loaded.
