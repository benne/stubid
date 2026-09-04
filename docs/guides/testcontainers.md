# Running StubID from a test suite

`StubId.Testing` starts StubID in Docker for the length of a test run. `StubId.InProcess` runs it
inside the test process instead, with no container at all, and is [its own guide](in-process.md).
`StubId.Client` is the control API on its own, for a suite that already has an instance running
somewhere.

```
dotnet add package StubId.Testing
```

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

## Serving TLS

Off by default, because the point of an emulator is being reached quickly and a transport nothing
trusts yet is a slower first hour. When you want it:

```csharp
await using var stub = new StubIdBuilder().WithTls().Build();
await stub.StartAsync();

// options.Authority = stub.Authority.ToString();  → https://localhost:32771/op
// options.BackchannelHttpHandler = stub.CreateTrustingHandler();
```

That is the whole configuration. In particular the .NET handler's `RequireHttpsMetadata` is left at
its default of true, which is the point: the usual advice for testing against a stub is to turn that
check off, and a relaxation added for a test is one copied line from a production client that accepts
an unsecured metadata document. `A_stock_client_signs_in_with_the_https_check_left_on` in
`tests/StubId.Testing.Tests/StockClientOverTlsTests.cs` is that claim, run in CI.

TLS **adds** a listener rather than replacing one. Plain HTTP keeps answering on 8080 and the control
API keeps using it, so creating a citizen never waits on a trust decision. Both listeners render the
same URLs, because the issuer is stored data rather than something derived from the request — there
is still exactly one issuer, and it names the secured port.

### Trusting it

`CreateTrustingHandler()` returns a handler that trusts **this instance's certificate and nothing
else**. It is not a handler that accepts anything: it compares what the server presented against the
exact certificate this container generated, which the module read over the plain-HTTP control API
during start.

That distinction is the reason the method exists. Waving validation through with a callback that
returns true is the shortcut everyone reaches for, and it is indistinguishable at a glance from code
that belongs nowhere near production.

StubID ships no certificate and installs nothing into any trust store. A self-signed one is generated
on first use and written into the keys volume, so it stays stable across restarts for the same reason
the signing keys do — a client that pinned what it saw gets a different answer after a restart
otherwise, and reports it as a trust failure rather than as a restart.

Trusting it from anything that is not .NET — a Node process, a JVM, `curl`, a browser you drive by
hand — is [its own guide](certificates.md). The certificate comes off the plain-HTTP control port as
PEM, so one `curl` gets it with no SDK involved. Driving a login from a browser you are automating
rather than driving by hand — where each engine trusts by a different mechanism — is
[another](browsers.md).

### Bringing your own

When the certificate has to chain to something the environment already trusts:

```csharp
new StubIdBuilder().WithTlsCertificate(File.ReadAllBytes("dev.pfx"), "password")
```

Or on a container you run yourself:

```
docker run -p 18080:8080 -p 18443:8443 \
  -e StubId__PublicBaseUrl=https://localhost:18443 \
  -e StubId__Tls=pkcs12 \
  -e StubId__Tls__Path=/tls/dev.pfx \
  -e StubId__Tls__Password=password \
  -v "$PWD/dev.pfx:/tls/dev.pfx:ro" ghcr.io/benne/stubid
```

`StubId__Tls=self-signed` generates one instead, and publishes its public half at
`GET /_stubid/v1/runtime/tls-certificate.pem`. Its subject alternative names cover `localhost`,
`127.0.0.1`, `::1` and the container hostname; add more with
`StubId__Tls__SubjectAlternativeNames=stubid,stubid.internal`. A certificate carrying no matching
name is refused by every current client, and none of them say so in the error.

## Deciding logins

How a login resolves is [its own guide](approvals.md). Two things matter here.

Queue the outcome before driving the application. A queued decision is consumed by the next
matching login, so the login is decided before anything could have waited on it — which is what
makes a suite fast and what makes an aborted login reproducible.

Approving a parked login on `/op/Login` does return the browser to the client with a code, so a
browser test can click through. A test without a browser has nothing to click, so queue the
outcome or leave automatic approval on: the decision is then made before anything could have
waited on it.

## Which image

The module runs the published image by default. Point it somewhere else with the constructor:

```csharp
new StubIdBuilder("ghcr.io/benne/stubid:2026.09.1")
```

Pin a version. Fidelity corrections change what StubID puts on the wire, and a suite asserting on
those bytes is a suite a floating tag can break.

The same release is spelled two ways, and both are correct. The container tag keeps its padding,
because tags sort as text and `2026.09` precedes `2026.10` where `2026.9` would not. NuGet reads a
version as numbers and drops the zero, so the package is `2026.9.1` — which is what a bare
`dotnet add package` writes into a project file. Asking for `--version 2026.09.1` resolves the same
package and is written back the way you typed it.

## What a test run costs

Measured in this repository's own CI, against the image it has just built: the container is ready
in about three seconds, and creating a citizen and driving a login through to a token takes about
fifty milliseconds. The first run on a machine that has not pulled the image pays for the pull,
which is 53 MB compressed and happens once; the module does not build one, and cannot, because
building would need this repository's Dockerfile in your working tree.

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
development instance, or [an in-process host](in-process.md).
