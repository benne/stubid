# Unreleased

Notes accumulate here as changes land, and this file is renamed to the version when a release
goes out. The dated files beside it are history and are never edited.

## Breaking

**Spelling is US English throughout.** The repository was written in British English and is now
written in American English, which is what the surrounding ecosystem already uses. Eight names
that shipped in 2026.09.1 changed with it:

| Was | Is |
| --- | --- |
| `BehaviourApi` | `BehaviorApi` |
| `StubIdClient.Behaviour` | `StubIdClient.Behavior` |
| `StubIdHost.Behaviour` | `StubIdHost.Behavior` |
| `StubIdContainer.Behaviour` | `StubIdContainer.Behavior` |
| `BrokerState.OrganisationOf` | `BrokerState.OrganizationOf` |
| `Client.Organisation` | `Client.Organization` |
| `PublicBaseUrl.TryNormalise` | `PublicBaseUrl.TryNormalize` |
| `POST /_stubid/v1/behaviours/enqueue` | `POST /_stubid/v1/behaviors/enqueue` |

Seven of the eight still work as they were. The old member names are `[Obsolete]` aliases that
forward to the new ones, and the old route is registered a second time onto the same handler, so
an existing suite compiles with a warning rather than an error and keeps answering over the wire.
**Those aliases go away in the release after this one**, which is the whole point of their being
here.

`BehaviourApi` is the exception: the type itself is gone rather than deprecated, because an alias
for it would have meant unsealing the class that replaced it. Code that only reaches the property
— `stub.Behaviour.EnqueueAsync(...)`, which is what the guides showed and what most suites write
— is unaffected. Code that names the type needs `var` or the new name.

One smaller edge: `Client.Organisation` is a read-only alias, so reading it works and
`with { Organisation = ... }` needs the new name.

`Tokens` takes `organization` where it took `organisation`, in `IdToken`, `UserInfo`,
`UserInfoToken`, `TransactionToken` and `Subject`. Positional calls are unaffected; only a caller
passing that argument by name has to change.

Nothing else moved. The admin interface, the issued-artifact reads and the queue reads all
arrived after 2026.09.1 and have never been in a package or an image, so they simply carry the
new spelling. No JSON field name that ever shipped changed.

**`POST /_stubid/v1/reset` clears more than it did.** It cleared the sessions and the queue; it
now also clears issued codes, access tokens, pushed requests and the CPR-match counters. A reset
that left credentials standing was surprising, and the issued page is where somebody first
notices. Citizens still survive a reset, which is the thing suites depend on.

## Not changed on purpose

The recorded fixtures keep their British spelling. Their `meta.json` files are hashed into a
manifest, and the session pack's manifest records a sitting that cannot be repeated — so the
spelling in a recording is part of the record rather than prose to be corrected.
