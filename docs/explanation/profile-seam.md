# The broker profile seam

StubID serves one broker today and is meant to serve several. The seam is where a broker's
personality goes, and the risk it manages is that an abstraction designed around one example
is usually wrong.

So it was designed against two: Signaturgruppen Broker, which is recorded and implemented, and
Idura, which is not implemented at all. Idura's route table is declared and served in
`src/StubId.Profiles.Idura` for one reason — to find out whether the seam can express a second
broker before anything is built on it.

## What is behind the seam so far

Only the route table. A profile declares patterns relative to the tenant root, the methods
each answers, how strictly each path is matched, and the handler.

That is deliberately less than the design allows. Claim composition, error envelopes, key
rosters and the request grammar are still engine code, and stay there until a second profile
actually needs them to differ. Each has exactly one implementation today, and the recordings
that would justify a second shape do not exist. Moving them now would be guessing with extra
steps.

## What Idura forced that one broker never would

| | Nets eID Broker | Idura |
| --- | --- | --- |
| Issuer | ends in `/op` | the bare host, no path |
| Routes | under `/op/...` | at the host root |
| Dynamic segment | none | base64 of an acr value, **before** `.well-known` |
| Path matching | first segment ordinal, rest not, trailing slash refused | case-insensitive, trailing slash tolerated |
| Configuration probe | none | status depends on the query string |

Four consequences, each of which would have been wrong if the seam had been cut around one
broker:

**Routes are relative, never absolute.** Nets eID Broker's `op` is the first segment of its own
pattern, not a mount prefix. That is what lets a document served from *under* a path segment
declare an issuer *without* one, which is exactly what Idura's acr-scoped discovery does.

**The dynamic segment applies to two routes, not all of them.** Idura answers 404 for the same
segment in front of its key set and its token endpoint. A stub that served them there would
pass a client the real broker refuses — the same false pass this project exists to prevent,
wearing different clothes.

**The segment is standard base64, not base64url.** Load-bearing rather than pedantic: `-` and
`_` are not standard-base64 characters, which is what stops a root-mounted tenant's dynamic
first segment from ever swallowing StubID's own `/_stubid/…` surface.

**Path strictness is per profile.** Both brokers are right about themselves. Being stricter
than a broker fails a client that works against it, which is the same class of error as being
looser, pointing the other way. StubID got this wrong in the other direction first, and a test
locked the mistake in until it was probed rather than assumed.

## Collisions stop the boot

Two profiles claiming one path do not fail fast on their own. The matcher is built lazily on
the first request, so the application starts happily and then throws on every request
afterwards; the compiler's duplicate-route analyser cannot help either, because it only sees
literal registrations in source.

The route set is therefore scanned as it loads. The check is conservative rather than a proof:
two parameters with different policies are allowed to share a position, because rejecting that
would reject the Idura route the seam was built for, and two policies that some single value
satisfies remain ambiguous at request time.
