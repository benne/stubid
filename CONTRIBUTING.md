# Contributing

## How this project is built

StubID is built with AI assistance. Stating that once, here, is the whole of it: the code,
the commit history, and the documentation are held to the same standard as any other
project, and are reviewed as such before they land.

## Writing

Everything with a reader — commits, documentation, issues, pull requests, release notes,
error messages — is written the way you would write for a colleague.

- Commit subjects are imperative and under 72 characters. The body explains why the change
  is needed. When a commit changes emulated behaviour, cite the fixture or the issue that
  justifies it.
- Documentation is plain and declarative. Say what something does and what it does not do.
  No marketing language, no emoji headings, no bulleted list where a sentence works.
- Issues and pull requests describe what changed, why, and how it was verified. A summary
  table that restates the diff helps nobody.
- Do not add per-commit AI trailers or links to assistant sessions. They are noise in a
  public history and the links are not reachable by anyone reading it.

## Rules specific to this project

**Do not paste vendor documentation prose.** Broker documentation is copyrighted.
Reimplement from observed behaviour and describe it in your own words. Protocol
identifiers, error codes, and claim names are functional facts and may be reproduced
exactly — as are the exact error strings the broker puts on the wire.

**New or changed emulated behaviour must cite a fixture.** A claim about what a broker does
belongs in `fixtures/`, recorded from the real thing, not in a commit message. Behaviour
derived only from vendor documentation ships marked as such and with its test skipped until
a recording confirms it. Prose has been wrong three times already; recordings have not.

**Never commit real personal data.** Fixtures are scrubbed before they land, and the build
fails if a CPR-shaped string, a real client secret, or an unscrubbed token appears in the
tree. The CPR generator produces replacement numbers (day of month 61-91) that cannot
collide with a real one.

**Contributions may use AI tools; you answer for the output.**
- The tool's terms must not restrict what this project can do with the output, and must not
  impose conditions beyond Apache-2.0.
- Output that reproduces third-party material is held to the same rule as any other
  copying: if the licence is unknown or incompatible, it does not land. If you would not
  paste it from someone else's repository, do not paste it from a model.
- Name the tools that materially contributed, in one line, in the pull-request description.
  Not in commit trailers — that rule stands.

## Before you open a pull request

```
dotnet build
dotnet test
dotnet pack
```

CI runs the same on Linux and Windows, plus the conformance suite against recorded
fixtures. Pack is there because a project added to `src/` produces a package by default,
whether or not that was the intention, and CI checks the set against the one we mean to
publish.

Every commit is signed off (`git commit -s`). The sign-off is the Developer Certificate of
Origin: your certification that you have the right to submit the work. It is a human
statement about provenance, not a tool credit, so the no-trailer rule does not touch it. CI
checks that each commit in a pull request carries one naming its author; `git rebase
--signoff` fixes a branch that does not.
