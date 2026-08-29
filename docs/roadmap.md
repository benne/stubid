# Roadmap

Status of the first emulated surface, Signaturgruppen Broker ("Nets eID Broker").

| | Milestone | Status |
| --- | --- | --- |
| M0 | Repository, licence, CI, day-zero probes | done |
| M1 | Recordings that need no login (discovery, JWKS, error shapes) | done |
| M1.5 | The manual recording pass (one login, signing, error paths) | not started |
| M2 | Token writer, keys, JWKS | done |
| M3 | First working login: a stock ASP.NET Core app signs in — **v0.1** | done, in process; container and Node/Spring outstanding |
| M4 | Broker profile seam, routing, fidelity ledger | not started |
| M5 | Sessions, approval resolution, citizens | not started |
| M6 | Full request surface, error fidelity, logout | not started |
| M7 | Control API, test modules, TLS trust, quickstarts | not started |
| M8 | Admin interface | not started |
| M9 | Transaction signing | not started |
| M10 | Release engineering | not started |
| M11 | Documentation site and generated broker reference | not started |
| M12 | v1.0 | not started |

Deferred past 1.0 on purpose: hosted multi-tenant service and its accounts, CIBA, PAdES
document wrapping, the Idura profile, and NemLog-in / OIOSAML.
