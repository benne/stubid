# Deciding what a login does

Against a real broker, a login waits for a person. Someone opens MitID's Test Tool,
finds the pending request and approves it. That is what makes an end-to-end test of a
MitID integration impractical: the step in the middle cannot be scripted.

StubID keeps the shape of that step and removes the person. A login still parks, still
has to be decided, and still ends in one of the outcomes the broker can produce. What
changes is who decides, and how quickly.

## Where a decision comes from

A parked login is resolved by the first rule that has an opinion. Tiers run in order and
a tier with no opinion is skipped, never treated as a refusal.

Tiers 2 and above run the moment a login parks, so most logins are decided before anyone
sees them. Tier 1 is different: it names one login, which means it can only arrive after
that login exists. It joins the same list afterwards.

| Tier | Source | Use |
| --- | --- | --- |
| 1 | A decision aimed at one login | `POST /_stubid/v1/sessions/{id}/approve` or `/reject`, and the login page's two buttons |
| 2 | A queued decision, consumed once | `POST /_stubid/v1/behaviours/enqueue` |
| 3 | The request's own `simulation` parameter | Names who logs in; drop-in for suites written against the broker's paid add-on |
| 4 | A rule on the chosen citizen | "Signing in as this person always aborts" |
| 8 | The configured default | Approve everything, or park and wait |

The default is to approve, because an instance that parks every login makes a test hang
rather than fail. Set `StubId:ApproveAutomatically=false` for an instance someone is
watching, and logins wait at tier 8 instead.

Tier 3 chooses an identity without settling the outcome, and tier 4 settles it. That
split matters: naming a person in the request says who is logging in, not that the login
succeeds.

Tiers 5 to 7 — rules scoped to a group, a client or a tenant — are numbered but not
implemented. They triple the precedence matrix and serve an audience this does not have
yet. The numbering is left with gaps so adding them later does not renumber anything a
test has already been written against.

Tier 2 is the one most suites want. Enqueue a decision, drive the application, and the
next login that matches consumes it:

```
POST /_stubid/v1/behaviours/enqueue
{ "approve": false, "clientId": "…", "errorCode": "mitid_user_aborted" }
```

The next login for that client fails, and the client sees exactly what the broker sends
for a user who aborted: a redirect back to the redirect URI with
`error=access_denied&error_description=mitid_user_aborted`. No broker add-on will
produce that on demand.

## Why the outcome was what it was

Precedence is unreadable from the outside, so every session keeps a record of the whole
ladder:

```
GET /_stubid/v1/sessions/{id}/explain
```

Every tier reports, including the ones that were skipped and why. A test that expected
its queued refusal to apply and got an approval instead can see in one call that some
earlier test's leftover decision was consumed first.

`POST /_stubid/v1/reset` clears the sessions and the queues, which is what keeps that
from happening. Citizens survive it, so a suite builds its people once.

## Terminal states are written once

A login ends as approved, refused or expired, and the first writer wins. The second one
gets `409 Conflict` carrying the outcome that actually happened, rather than an
exception or a silent overwrite.

This is not a theoretical race. "The tester clicked approve as the timeout fired" is an
ordinary event in a suite that exercises timeouts, and it is the usual source of a test
that fails once a fortnight. Here it is decided, not raced: one of the two writers wins,
both learn the same answer.

## Timeouts without waiting

The broker gives a login about five minutes. Waiting that out in CI is not an option, so
the clock is injected rather than read from the machine. Start with
`StubId:ControllableClock=true` and move it:

```
POST /_stubid/v1/time/advance
{ "seconds": 301 }
```

The session expires as it would have, the test finishes in milliseconds, and nothing
sleeps.

## Citizens

A citizen is created with the properties MitID would carry: a name, a date of birth, a
personal number, and the assurance levels and authenticator the login reports.

```
POST /_stubid/v1/citizens
{ "name": "Karen Refsgaard", "dateOfBirth": "1979-11-02", "gender": "female" }
```

A citizen can carry a rule, which is what a login as that person does:

```
POST /_stubid/v1/citizens
{ "name": "Test Person", "id": "aborts", "dateOfBirth": "1990-01-01",
  "rule": "mitid_user_aborted" }
```

Every login as `aborts` fails with that code — through the simulation parameter, through
the login page, and through an explicit `approve` naming them. A rule is not an override
of an approval so much as what approving that person means, so a suite sets the person up
once and then writes ordinary tests.

A queued decision is the exception. Tier 2 queues an outcome rather than a person, so
enqueueing an approval for `aborts` approves — which is how you sign in as someone whose
rule you want to ignore for one test.

The personal number is generated, never supplied, and it is always a replacement number:
the day of month is raised into the 61–91 range, which no issued CPR number uses. A
number StubID produces cannot belong to a person. See
[CPR and test data](../explanation/cpr-and-test-data.md) for why that matters more than the modulus-11
check people expect.

## The login page

A parked login redirects the browser to `/op/Login`, where it can be approved by hand.
The page is deliberately StubID's own: no MitID logo, no copy of the authenticator, and
a line on the page saying no identity is being verified. Reproducing the real UI would
put someone else's trade dress on an emulator, and a page that looked convincing is a
page someone can be fooled by.

One thing from the request does reach it: a transaction text, decoded and escaped, under a
heading of its own. A person being asked to approve something is entitled to see what, and
the broker puts that text on its own page too. Nothing else the request carried is shown —
not the client's name, not even its `client_id`.

It submits to the same store the control API writes to, so a manual click and an API
call are the same code path, not two implementations that agree until one of them
changes.
