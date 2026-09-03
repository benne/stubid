# Roadmap

Status of the first emulated surface, Signaturgruppen Broker ("Nets eID Broker").

| | Milestone | Status |
| --- | --- | --- |
| M0 | Repository, licence, CI, day-zero probes | done |
| M1 | Recordings that need no login (discovery, JWKS, error shapes) | done |
| M1.5 | The manual recording pass (one login, signing, error paths) | done |
| M2 | Token writer, keys, JWKS | done |
| M3 | First working login: a stock ASP.NET Core app signs in — **v0.1** | done |
| M4 | Broker profile seam, routing, fidelity ledger | routes and ledger done; claims, errors and keys still engine |
| M5 | Sessions, approval resolution, citizens | done |
| M6 | Full request surface, error fidelity, logout | done |
| M7 | Control API, test modules, TLS trust, quickstarts | control API, both test modules, TLS, certificate trust and the browser matrix done; the remaining quickstarts not started |
| M8 | Admin interface | not started |
| M9 | Transaction signing | the transaction token, its OCSP response and the reference text done; the transaction text and request objects not started |
| M10 | Release engineering | not started |
| M11 | Documentation site and generated broker reference | not started |
| M12 | v1.0 | not started |

Deferred past 1.0 on purpose: hosted multi-tenant service and its accounts, CIBA, PAdES
document wrapping, the Idura profile, and NemLog-in / OIOSAML.

The transaction token's text claims are recorded. A second sitting on 2026-09-02 sent a signed
request carrying a transaction text and took CAP-031, which settles the last row a login could
close; what came back is in [the claims reference](brokers/neb/claims.md). Still unseen: the
address members under `ssn.details_*`, because the test identity has no register entry behind
it.

Three behaviours are implemented from documentation rather than from a recording, because
reaching them needs a completed login: end session honouring a post-logout redirect when it
is given a valid `id_token_hint`, the CPR-match refusal after three attempts, and
`prompt=none` answering `login_required`. Each is marked as such in the fidelity ledger and
listed in [the divergences](brokers/neb/divergences.md).
