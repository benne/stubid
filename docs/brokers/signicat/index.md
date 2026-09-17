# Signicat

The second broker StubID serves, and the first one it only declares. An instance chooses it with
`StubId:Profile=signicat`, or `WithProfile("signicat")` on either hosting package, and answers on
`/auth/open` instead of `/op`.

What is served is what recordings settle: the discovery document and the key set. Everything else
the document advertises answers 501 with a link to the reason. No login can be completed here,
because no login has been recorded — that needs a person in MitID's test tool, and until it happens
the claims, the request grammar and the way this broker refuses a login are unknown. Guessing them
is the one thing this project does not do.

| | |
| --- | --- |
| [Where StubID differs on purpose](divergences.md) | what is not reproduced and why, and the whole fidelity ledger for this broker |

The recordings are in `fixtures/signicat/sandbox/`, made against a sandbox account of the project's
own rather than a customer's. A capture is named `CAP-nnn`, and the numbering restarts per broker:
`CAP-001` here is this broker's discovery document, not the first broker's.

## What the discovery document says

It is served as recorded, with this instance's address in place of the tenant's, so the issuer ends
in `/auth/open`. Two consequences are worth knowing before a suite depends on it.

`claims_supported` is the recorded account's own configuration rather than a fact about the
platform: Signicat lets an account alias and extend claims, and the list names 334 of them. A
document composed from a model would have had to invent that list, which is why the recording is
served instead.

The document advertises endpoints this build does not reproduce, and that is deliberate. Trimming
it would be less faithful, not more — a client library that keys off a member being absent would
behave differently here than against the broker.
