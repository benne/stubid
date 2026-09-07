# API reference

The four packages a reader installs. Everything here is generated from the doc comments in the
source, and those four projects are held to having them: an undocumented public member fails
their build.

| | |
| --- | --- |
| `StubId.Client` | the typed client for the control API — citizens, sessions, queued decisions, the clock, and what can be changed while an instance runs |
| `StubId.Testing` | the Testcontainers module, which starts an instance and tells it the address this process reaches it at |
| `StubId.InProcess` | the same instance hosted inside the test process, with no Docker |
| `StubId.Abstractions` | the fidelity vocabulary: what a claim about the broker has to say to count as one |

`StubId.Server`, `StubId.Wire` and `StubId.Profiles.Abstractions` are published too and are not
here. Almost all of their public surface is engine internals, public only so the hosting packages
can reach them, and pages for those would bury the surface anybody calls.

What the emulated broker answers under `/op` is not an API of ours and is not described here. It
is the broker's, and it is written down in [the broker reference](../brokers/neb/index.md).
