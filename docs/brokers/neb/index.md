# Nets eID Broker

What this broker does, written from recordings of its pre-production environment rather than from
its documentation. The two disagreed often, and the recording won every time.

| | |
| --- | --- |
| [What the tokens carry](claims.md) | every member of every token, in the order the broker sends them |
| [How it refuses things](errors.md) | the four different ways it says no, and which one you get |
| [Request parameters](request-parameters.md) | its own parameters, and whether a bad one is refused at the authorize endpoint |
| [Where StubID differs on purpose](divergences.md) | every deliberate decision, what it costs, and the whole fidelity ledger |

The recordings themselves are in `fixtures/`, hashed into a manifest. A capture is named
`CAP-nnn` throughout, and every one of those citations is checked by the build against a
directory that has to be there.
