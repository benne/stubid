# Running StubID inside your test process

`StubId.InProcess` hosts StubID on an in-memory transport in the process running your tests.
There is no container, no port and no listener, so there is also nothing to install, nothing to
wait for and nothing to clean up.

```
dotnet add package StubId.InProcess
```

```csharp
await using var stub = new StubIdHostBuilder().Build();
await stub.StartAsync();

var citizen = await stub.Citizens.CreateAsync(
    new CitizenSpec { Name = "Anders Berg Christiansen", DateOfBirth = new DateOnly(1985, 3, 29) });

await stub.Behaviour.EnqueueAsync(Decision.Approved(citizen.Id).ForClient(clientId));

// Drive the application under test. Configure it with stub.Authority and nothing else.
```

That snippet is not decoration: it is the opening of `A_citizen_created_from_a_test_signs_in_through_the_host`
in `tests/StubId.InProcess.Tests/InProcessLoginTests.cs`, which runs in CI on Linux and on Windows.
A copied example that has quietly stopped working is worse than no example.

## Pointing a client library at it

Two properties, and neither of them relaxes anything:

```csharp
options.Authority = stub.Authority.ToString();        // https://stubid-inprocess.invalid/op
options.BackchannelHttpHandler = stub.CreateHandler();
```

**The handler's `RequireHttpsMetadata` is left at its default of true.** The usual advice for
testing against a stub is to turn that check off, and a relaxation added for a test is one copied
line from a production client that accepts an unsecured metadata document. It is not needed here,
because the check is on the scheme of the metadata address and the authority is https.

`A_stock_client_reaches_the_module_with_the_https_check_left_on` in
`tests/StubId.InProcess.Tests/StockClientTests.cs` is that claim. It drives one challenge rather
than a whole sign-in; the complete login through cookies and the form post is
`tests/StubId.Interop.AspNetCore/StockClientTests.cs`, against the same server.

## Why the address is a name that cannot resolve

An emulated broker cannot derive its own issuer from the request that asked for it, so the address
is stored data. A container has to be told it after Docker assigns a port. Here you chose it, so
it is known before anything starts — `stub.Authority` is readable on an instance you have not
started yet, which is what lets you configure a relying party first.

The default is `https://stubid-inprocess.invalid`. Nothing dials it: the back channel is the
handler above and the front channel is `stub.CreateClient()`. The name exists for the case where
you forget the handler, and `.invalid` is reserved by RFC 2606 precisely so that nothing resolves
it — so the failure names the host it could not find, rather than reaching whatever else happens
to answer on that machine.

Pin your own when you want one:

```csharp
new StubIdHostBuilder().WithPublicBaseUrl(new Uri("https://stubid.example"))
```

## There is no TLS switch

The container module has `WithTls()`, and this one deliberately does not. There is no socket here,
so there is no listener to secure and no certificate to present — and none is needed, because the
authority is https either way. Asking for one through `WithSetting("StubId:Tls", …)` is refused
rather than ignored: accepting it would have the instance write a certificate that nothing serves
and then report over the control API that it serves TLS.

`stub.CreateHandler()` is the twin of the container module's `CreateTrustingHandler()`, and it is
simpler for the reason that makes it simpler. There is no transport, so there is nothing to trust.
Validation is not being waved through; there is nothing to validate.

## What this cannot do

**Anything that has to dial StubID needs the container.** A browser, the Node and Spring suites, an
application under test that is not .NET, or two processes sharing one instance all need a real
socket, and this module has none. Use [`StubId.Testing`](testcontainers.md) for those.

A loopback listener would remove that limit and may be added later. It is not designed yet, and
guessing at it here would be worse than saying so.

## Where the keys live

`%TEMP%/stubid-keys`, shared by every instance on the machine, for the same reason the container
mounts a volume: clients cache discovery metadata for hours, so keys regenerated between runs fail
every integration at once with nothing on their side to explain it. The first instance on a machine
generates them; every one after that loads them.

Point somewhere else when a test wants its own, and delete the directory afterwards:

```csharp
new StubIdHostBuilder().WithKeyPath(Path.Combine(Path.GetTempPath(), $"stubid-{Guid.NewGuid():N}"))
```

Instances share that directory safely — whichever loses the race to create a key keeps the
winner's, which is the point of writing it down at all.

## Two instances at once

Nothing prevents it. Every piece of state an instance keeps is its own, so two hosts in one process
have separate citizens, sessions and issuers; the only thing they share is the key directory above.
`tests/StubId.InProcess.Tests/MultipleHostTests.cs` starts two at the same time and checks both
halves of that.

## Seeing what it did

Silent by default, because a host that logs every request at information level puts its own noise
into your test output. When you want it:

```csharp
new StubIdHostBuilder().WithLogging(logging => logging.AddConsole())
```

That is the in-process equivalent of reading a container's logs. `stub.Services` is the other
thing only an in-process host can offer — the instance's own service provider, for reaching a
collaborator the control API does not expose. Code written against it does not move to a container
unchanged, which is the trade.

## Deciding logins

How a login resolves is [its own guide](approvals.md), and it is the same ladder either way. Two
things matter here.

Queue the outcome before driving the application. A queued decision is consumed by the next
matching login, so the login is decided before anything could have waited on it.

Approving a parked login on `/op/Login` does return the browser to the client with a code, so a
browser test can click through. A test without a browser has nothing to click, so queue the
outcome or leave automatic approval on: the decision is then made before anything could have
waited on it.

## What a test run costs

On an ordinary development machine: an instance is started and ready in about 150 milliseconds
against a machine that already has keys, and creating a citizen and driving a login through to a
token takes about 230 milliseconds. Both numbers are reported by the tests that budget them —
`An_instance_starts_against_a_warm_key_directory_in_under_a_second` and the login test above — and
both run in CI on every change, so a regression that made either pathological would fail the build
rather than go unnoticed.

The very first instance on a machine also generates three signing keys, which is a one-off cost
and is why the startup test warms the directory itself before timing anything.

Cheap enough that each test can build its own instance, which is what the tests here do. Call
`ResetAsync()` between tests instead when you would rather share one: it clears the sessions,
anything queued and everything issued, and keeps the citizens.

## Or the container instead

`StubId.Client` has no Docker and no hosting dependency, and works against any instance that is
already running:

```csharp
using var stub = new StubIdClient(new Uri("http://localhost:8080"));
```

That is the shape for [the compose recipe](../../samples/compose/docker-compose.yml) or a shared
development instance. Running one in Docker for the length of a test run is
[its own guide](testcontainers.md).
