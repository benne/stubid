# Unreleased

Notes accumulate here as changes land, and this file is renamed to the version when a release
goes out. The dated files beside it are history and are never edited.

## The site has a mark of its own

`stubid.dev` shipped with docfx's stock icon in the tab and the header. It now carries one drawn
for the project: an ID card as a dashed outline around a person, which is the placeholder
convention doing the work the name does — identity-shaped, and openly not the real thing. Nothing
about it approximates any broker's logo, which the non-affiliation notice in `README.md` makes a
requirement rather than a preference.

The tab icon is an `.ico` carrying 16, 32 and 48 pixel cuts rather than a scaled vector, because
a dashed outline is exactly the shape a browser downscales badly; the header takes the vector.
Both sit at the site root, where they displace the two files the template would otherwise put
there, so a browser that probes `/favicon.ico` without reading the tag gets this one too.

## The research notes are no longer on the site

`stubid.dev` carried four research notes — the day-zero probes, the observed tokens, the signed
requests and the transaction screens. They are the measurements the broker reference was built
from: working notes, dated and kept as written, rather than documentation for somebody using the
emulator. The site now stops at what a reader needs, and the notes stay in the repository where
they are still cited by name.

Four pages in the broker reference link to them and now do so by full URL, which is what the
guides already do for the two files under `samples/`. Anything bookmarked under
`stubid.dev/research/` moves to `github.com/benne/stubid/blob/master/docs/research/`.

The files themselves have not moved and will not: `docs/research/signed-requests.md` is the
evidence behind two entries in the fidelity ledger, and `GET /_stubid/v1/fidelity` serves that
path as a string. Relocating it would change an answer on the wire to make a navigation menu
shorter.

The runbook for taking a recording stays on the site, under Explanation rather than beside the
notes it is not one of.
