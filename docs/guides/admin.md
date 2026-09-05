# Watching and steering an instance

The other guides put StubID inside a test suite or behind your own application. This one is about
the instance itself. Every instance serves a set of pages where you can watch logins arrive, decide
them by hand, set up the people they sign in as, and read what this build actually emulates —
without writing a line of code.

```
docker run -d --name stubid -p 8080:8080 -v stubid-keys:/keys \
  -e StubId__PublicBaseUrl=http://localhost:8080 ghcr.io/benne/stubid
```

Then open <http://localhost:8080/_stubid/admin>. It is served on whichever listeners the instance
has, so an instance with TLS answers on the secured port as well.

## Logins arrive on their own

The first page is the logins, newest first, with what each one is for and how long it has left. It
keeps itself current: a small script asks the instance for the rows every couple of seconds and puts
them on the page. With JavaScript switched off the table is still there, rendered by the server, and
the Refresh link reloads it — the line saying it updates on its own stays hidden, because it would
not be true.

One consequence worth knowing. StubID expires a login by noticing rather than by a timer, so reading
the list is what makes an expiry happen. An open page therefore expires logins promptly instead of
whenever something next looks. That is closer to what a real broker does, not further from it, and
under a controllable clock nothing moves unless a test moves it.

## Deciding one is the decision a test makes

Open a login and you get what the client asked for, the transaction text if it sent one, and the
ladder that decided it tier by tier. If nothing has decided it yet, there are buttons.

They write to the same store the control API writes to. That is not a claim about tidiness: the
ladder records which door a decision came through, so a login decided on the page explains itself as
`the admin page` where one decided by a test says `the control API`. There is one code path with
three doors onto it, rather than three implementations that agree until one of them changes.

The citizen's own rule still applies. Approving as somebody set to fail fails, whoever pressed the
button, because that is what approving as that person means.

## Setting up what decides them

The people page adds and removes the citizens a login can sign in as, and changes the one field on
them worth changing while something is running: whether signing in as them works at all. Leave the
box empty and they approve; put a broker error code in it and they refuse with it.

Nothing else about a person is editable, and that is deliberate. The personal number is derived when
they are created, from the date of birth and a sequence, so moving a birthday without it would leave
somebody whose number disagrees with their own age. Delete and add again, which is one click. The
number is always a replacement number and cannot belong to anybody — [CPR and test
data](../explanation/cpr-and-test-data.md) explains why that matters more than the checksum people
expect.

The queue page shows what is waiting to be taken by the next login, which nothing could see before.
A decision queued by one test and spent by the next is the hardest kind of surprise to explain from
outside, and this is where you look. Reading it does not consume it.

## What this build is, and what it has handed out

Two pages that are generated rather than written. The first reads the routes this instance actually
loaded, the fidelity ledger from the attributes on the code that emits each answer, the three
clients it publishes, its signing key ids and its certificate. Nothing on it is a list somebody
maintains beside the code, so it cannot describe a build that no longer exists.

The second is what the instance has issued: the pushed requests, codes and access tokens, with who
got one and for which login. Never the value. A code and an access token are credentials, and these
pages ask nobody who they are, so printing one would turn "see what this instance issued" into
"issue yourself a token as anybody". The login is a link instead, and it goes to that login's own
page, which is where a value would otherwise have been used to work out what belonged to what.

## Steering it while it runs

The controls page does four things. It sets the address the instance answers as, which every issuer
it emits is built from. It moves the clock, on an instance started with
`StubId__ControllableClock=true`. It clears the logins, the queue and everything issued, keeping the
people. And it switches whether logins decide themselves.

That last one is why a demonstration does not need a different container. An instance approves
automatically by default, because a test that hangs waiting for a person is worse than one that
never exercises the waiting; switch it off and logins park until somebody decides them. It is an
override rather than a change to the setting, so the page can also put the instance back to whatever
it was started with — and a suite sharing one instance can borrow the behaviour for one test and
hand it back.

## There is no password on any of this

Anything that can reach the port can create citizens, decide logins, move the clock and reset the
instance. There is no authentication on the admin pages or on the control API, and no setting that
adds one.

That is deliberate rather than unfinished. A test module cannot hold a credential nobody gave it,
and an emulator that demanded one would be misconfigured in CI more often than it was secured. So
the boundary is the port: a developer machine, a CI job's own network, a compose network. Not a
shared host, and not an address anybody else can reach.

The pages do widen one thing, and it is worth naming rather than glossing. A page on another origin
has always been able to post to `/_stubid/v1/reset`, because that route takes no body; the rest of
the control API binds JSON and refuses a plain form outright. These pages are forms, so a page you
merely visit can now also add a citizen, queue a decision, move the clock or re-point the address
this instance answers as. It cannot read anything back, and the changes take anyway.

They carry no anti-forgery token, deliberately. One would close that particular route and would not
move the boundary: anything that can reach the port can still do all of it directly, and a login
screen on pages sitting beside a control API that has none would imply a protection that is not
there. An instance somewhere a hostile page might find it is already somewhere it should not be.

What an attacker on that port gains is the ability to sign in to *your application under test* as
anybody, which is precisely what the tool is for. There is nothing else to take. The personal
numbers are replacement numbers by construction, the private keys have no route that reaches them,
and every response carries `X-StubID-Emulator` so anything that finds an instance can tell what it
is.

## What this does not do

It needs a real socket, so it is the container or nothing: an in-process host runs on a test server
with no address to open in a browser, which [the in-process guide](in-process.md) says plainly.

It does not push. The table polls, and closing the page stops it; there is no stream to keep open
and nothing to reconnect.

It does not register clients. The three are fixed and StubID refuses any other client id, which is
the same for every guide here.

And it shows no token values, ever, for the reason above.

## Or from a test instead

Everything these pages do, a test can do without them. How a login is decided, and how to ask why it
went the way it did, is in [its own guide](approvals.md). Running StubID from a test suite is in
[the Testcontainers guide](testcontainers.md) for the container and in
[the in-process guide](in-process.md) for a host inside the test process. Seeing a login work
against an application you can start yourself is in [signing in](signing-in.md).
