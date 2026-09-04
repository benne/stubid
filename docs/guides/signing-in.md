# Signing in from your own application

The other guides put StubID inside a test suite. This one is the shortest path from nothing to a
MitID login you can look at: an application that starts, a browser, and a page of claims at the end
of it. What you sign in to is a stock ASP.NET Core application that names StubID in three lines.

You need Docker, the .NET SDK, and the development certificate most .NET machines already have
(`dotnet dev-certs https --trust`). Then:

```
docker run -d --name stubid -p 8080:8080 -p 8443:8443 -v stubid-keys:/keys \
  -e StubId__Tls=self-signed -e StubId__PublicBaseUrl=https://localhost:8443 \
  ghcr.io/benne/stubid

cd samples/aspnetcore && dotnet run
```

Open <https://localhost:5099>, follow the sign-in link, and you are looking at what MitID would have
told your application about the person who signed in. Nobody approves anything: a fresh instance
already has a citizen, and it approves by default.

That sample is not decoration. `The_sample_signs_a_citizen_in` in
`tests/StubId.Testing.Tests/SampleApplicationTests.cs` hosts that same `Program.cs` - not a copy of
it - against a container, on every change. A copied example that has quietly stopped working is
worse than no example.

The test beside it matters as much. A login that succeeds proves nothing about the certificate
handling below, because a sample that trusted every certificate would pass it too. That was
measured rather than assumed: replacing the check with one that accepts anything leaves the login
green, and only `The_sample_refuses_a_certificate_it_did_not_pin` notices.

## The three lines that name StubID

Two of them are the authority and the client, which is the whole adoption claim: an application
changes its authority and its credentials, and nothing else.

The third is a certificate. StubID generates its own, so nothing on your machine vouches for it, and
the .NET handler will not fetch metadata over https it cannot verify. The sample reads the
certificate from the plain-HTTP control port - a bootstrap that needs no trust, because nothing has
to be believed in order to fetch it - and tells its back channel to accept that one certificate and
no other.

What it deliberately does not do is turn `RequireHttpsMetadata` off. That is the usual advice for
testing against a stub, and it is one copied line away from a production client that accepts an
unsecured metadata document. The line is never written here, so it cannot be copied.

In your own project, the project reference in `StubId.Sample.AspNetCore.csproj` is
`dotnet add package StubId.Client`.

## Why the claims come from userinfo

The id_token this broker issues says `idtoken_type: strict`, and carries no `mitid` claim at all: no
name, no CPR flag, no date of birth. Those are on the userinfo endpoint. So the sample sets
`GetClaimsFromUserInfoEndpoint`, and against the real broker you would need it for the same reason.
What the two responses each carry is in [the claims reference](../brokers/neb/claims.md).

That alone still shows you nothing, because the handler keeps only the claims it has a mapping for
and it has never heard of any claim this broker names. `ClaimActions.MapAll()` is what puts them on
the page. It also stops the handler discarding the protocol claims it normally drops, so `iss`,
`exp` and `at_hash` end up in your authentication cookie as well - fine for a page whose whole job
is to show what arrived, and worth narrowing in an application that needs three claims and a
smaller cookie.

## The client is one of three, and you cannot bring your own

StubID registers three clients, in one organisation, and refuses any other `client_id` outright.
The sample uses the one that asks for a code. They are the same three every guide here uses, and
the secret is not checked - StubID accepts any, which is the same trade as not verifying an
`id_token_hint` it did not issue.

## The browser will warn you once

The authority is `https://localhost:8443`, secured by a certificate StubID generated a moment ago,
so the browser will say so before it lets you through. Accepting it once is enough for a look
around. Making it stop happening - per browser, per stack, and on the operating system - is
[its own guide](certificates.md), and three of those recipes run in CI on every change.

## Watching a login be decided instead

Approving by default is what a test wants and not what a demonstration wants. Start the container
with `-e StubId__ApproveAutomatically=false` and the login parks instead: the browser lands on
StubID's own page, and nothing continues until somebody approves or aborts there. That page is
deliberately StubID's own, with no MitID logo on it.

Abort it once, because the refused path is the one applications get wrong. The browser comes back to
the application with `error=access_denied` and `error_description=mitid_user_aborted`, which is the
pair the real broker sends, and the sample renders both. The second one is the broker's own naming
and the thing worth logging, so a client that answers a refusal with a bare status code has thrown
away the only part that says what happened.

Nothing about that is StubID-specific. ASP.NET Core already separates the two: a refusal arrives at
`OnAccessDenied`, and a genuine fault - a correlation cookie that did not survive, a token that
failed validation - arrives at `OnRemoteFailure`. The sample answers them differently for the same
reason a real application would, because somebody aborting is an outcome and not an error.

A test would decide the same login through the control API without a browser at all, and both go
through the same store rather than two implementations that agree until one changes. How a login is
decided, and how to ask why it went the way it did, is in [its own guide](approvals.md).

## Signing out

The sign-out link ends both sessions: the application's cookie, and the session inside StubID, which
is what an `id_token_hint` is for. The browser comes back to the application afterwards, and asking
for the protected page again starts a fresh login.

## Node, with openid-client

`samples/node/signin.mjs` is a complete sign-in with `openid-client` - discovery, PKCE, the token
exchange, and userinfo - and it runs in CI on every change, over plain HTTP and again over TLS with
nothing relaxed. It is a starting point to copy from and a check at the same time; the assertions in
it are the half a sample would not have.

It wants its own instance, without TLS, so stop the secured one first:

```
docker rm -f stubid
docker run -d --name stubid -p 8080:8080 ghcr.io/benne/stubid

cd samples/node && npm install && node signin.mjs
```

A second instance rather than the first one, because there is exactly one issuer and it names the
address the instance was told about. The instance above publishes `https://localhost:8443/op` and
answers on 8080 as well - plain HTTP never stops - but everything it renders still names the
secured port. `openid-client` compares the issuer it discovers against the authority it was
configured with, character for character, so pointed at 8080 on that instance it refuses. Finding
that refusal is most of why the Node check exists. Reaching the secured port properly needs the
certificate, which [the certificates guide](certificates.md) covers for Node.

## Java, with Spring Security

`tests/interop-spring` resolves StubID's metadata the way a Spring application does, and asserts
that the issuer and all three endpoints come back right. It does not sign in, and there is no Spring
sample to run: what it covers is the part Spring is strictest about, which is deriving candidate
metadata locations from an issuer that carries a path segment and then checking the issuer it finds.
Everything after that is ordinary OAuth that Spring does the same way against any provider.

## A browser driving it

Driving the front channel from a real browser - three engines, each refusing the certificate first
and then trusting it by its own mechanism - is [its own guide](browsers.md), and the script it
explains is what runs in CI.

## Or from a test suite instead

Once you have seen it work, the useful thing is to have it in tests. Running StubID from a test
suite is in [the Testcontainers guide](testcontainers.md) for the container, and in
[the in-process guide](in-process.md) for a host inside the test process, which starts in about 150
milliseconds and needs no Docker at all.
