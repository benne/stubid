# Unreleased

Notes accumulate here as changes land, and this file is renamed to the version when a release
goes out. The dated files beside it are history and are never edited.

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
