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
exactly.

**New or changed emulated behaviour must cite a fixture.** A claim about what a broker does
belongs in `fixtures/`, recorded from the real thing, not in a commit message. Behaviour
derived only from vendor documentation ships marked as such and with its test skipped until
a recording confirms it. Prose has been wrong three times already; recordings have not.

**Never commit real personal data.** Fixtures are scrubbed before they land, and the build
fails if a CPR-shaped string, a real client secret, or an unscrubbed token appears in the
tree. The CPR generator produces replacement numbers (day of month 61-91) that cannot
collide with a real one.

## Before you open a pull request

```
dotnet build
dotnet test
```

CI runs the same on Linux and Windows, plus the conformance suite against recorded
fixtures.
