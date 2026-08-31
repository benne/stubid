# Running StubID from a test suite

`StubId.Testing` starts StubID in Docker for the length of a test run. `StubId.Client` is the
control API on its own, for a suite that already has an instance running somewhere.

```csharp
await using var stub = new StubIdBuilder()
    .WithControllableClock()
    .Build();

await stub.StartAsync();

var citizen = await stub.Citizens.CreateAsync(
    new CitizenSpec { Name = "Anders Berg Christiansen", DateOfBirth = new DateOnly(1985, 3, 29) });

await stub.Behaviour.EnqueueAsync(Decision.Approved(citizen.Id).ForClient(clientId));

// Drive the application under test. Configure it with stub.Authority and nothing else.
```

That snippet is not decoration: it is the body of `A_citizen_created_from_a_test_signs_in_through_the_container`
in `tests/StubId.Testing.Tests/ContainerLoginTests.cs`, which runs in CI against the image the same
job built. A copied example that has quietly stopped working is worse than no example.

`stub.Authority` is what a client library is configured with. The issuer it then discovers is that
string character for character, which is the comparison `openid-client` and Spring Security both
make and neither forgives.

## Why the address has to be told to it

An emulated broker cannot derive its own issuer from the request that asked for it. A browser
reaching a container on a mapped port and an application reaching the same container by service
name would discover two different issuers from one instance, and each would be wrong for the other
caller.

So the address is stored data. Docker assigns the host port when it starts the container, which
means the correct value is not knowable when the process starts — the module reads the mapped port
and sets it over the control API before reporting the container ready. Nothing that discovers a
document sees an issuer that was guessed.

The consequence for anything not started by the module: **an instance has to be told its address.**

```
docker run -p 18080:8080 -e StubId__PublicBaseUrl=http://localhost:18080 ghcr.io/benne/stubid
```

Without it, StubID answers `503` with `the public base URL is not set` rather than serving a
plausible wrong issuer, and `GET /_stubid/health/ready` reports the same until something sets one.
A `PUT` to `/_stubid/v1/runtime/public-base-url` sets it on a running instance. That value lives in
memory, so an instance restarted outside the module has to be told again.

## Pinning it yourself

When the browser and the application reach StubID by different names — a compose network, a proxy —
both still have to see one issuer, so pin it to the name the browser uses:

```csharp
new StubIdBuilder().WithPublicBaseUrl(new Uri("http://localhost:8080"))
```

The module then leaves the address alone. `stub.MappedAddress` is still where this process reaches
the container, which is not the same thing and is what the control API uses.

## Deciding logins

How a login resolves is [its own guide](approvals.md). Two things matter here.

Queue the outcome before driving the application. A queued decision is consumed by the next
matching login, so the login is decided before anything could have waited on it — which is what
makes a suite fast and what makes an aborted login reproducible.

Approving a login after it has parked settles its state, but it does not produce an authorization
code for the browser waiting on the redirect. A login that parks cannot be resumed. Queue the
outcome, or leave automatic approval on.

## Which image

The module runs the published image by default. Point it somewhere else with the constructor:

```csharp
new StubIdBuilder("ghcr.io/benne/stubid:2026.08.1")
```

Pin a version. Fidelity corrections change what StubID puts on the wire, and a suite asserting on
those bytes is a suite a floating tag can break.

## What a test run costs

Measured in this repository's own CI, against the image it has just built: the container is ready
in about three seconds, and creating a citizen and driving a login through to a token takes about
fifty milliseconds. The first run on a machine with no image builds one, which is a minute or so
and happens once.

Reuse (`WithReuse(true)`) keeps an instance between runs; it needs
`testcontainers.reuse.enable=true` in `~/.testcontainers.properties`. Call `ResetAsync()` between
tests: it clears the sessions and anything queued, and keeps the citizens, so a suite builds its
people once.

## Running the container yourself instead

`StubId.Client` has no Docker dependency and works against any instance:

```csharp
using var stub = new StubIdClient(new Uri("http://localhost:8080"));
```

That is the shape for [the compose recipe](../../samples/compose/docker-compose.yml), a shared
development instance, or an in-process host.
