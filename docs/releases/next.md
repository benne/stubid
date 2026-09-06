# Unreleased

Notes accumulate here as changes land, and this file is renamed to the version when a release
goes out. The dated files beside it are history and are never edited.

## The fidelity ledger gained a divergence it should always have carried

`GET /_stubid/v1/fidelity` now reports that the `id_token_hint` at the end-session endpoint is
read rather than verified. The broker checks that hint; StubID accepts any three-part token whose
payload carries a `sid`, including one it never issued. That was written up in the divergences
from the start and annotated nowhere, so the one place a running instance could be asked about it
did not know. Nothing about what the emulator answers has changed.
