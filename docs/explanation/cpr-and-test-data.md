# CPR numbers and test data

A MitID login can carry a Danish personal number, so an emulator of one has to produce
personal numbers. That raises an obvious question: whose?

The answer has to be nobody's, and it has to be nobody's by construction rather than by
luck.

## What people expect, and why it does not work

The usual instinct is to generate a number that fails the modulus-11 check, on the
theory that an invalid number cannot belong to anyone. That has not been true since
2007. CPR ran out of check-digit-valid serials and began issuing numbers that fail
modulus 11, so the check no longer separates real from invented. A generator that relies
on it produces numbers that may well be somebody's, and a validator that relies on it
rejects real people.

## What StubID does instead

CPR reserves a range for replacement numbers, used when a person needs a number before
their date of birth is established. The day of month is raised by 60, giving days 61 to
91 — values no ordinary date can produce.

StubID generates only in that range. `31 03 79` becomes `91 03 79`. The number keeps its
shape, its length, its digits and its gender parity, so anything that parses a CPR
number still parses it, and no arrangement of a real birth date can collide with it.

```csharp
Cpr.Generate(new DateOnly(1979, 11, 2), Gender.Female);   // day 62, not 02
Cpr.IsReplacementNumber(generated);                        // true
```

The last digit still carries gender — odd for men, even for women — because
applications read it, and a generator that ignored it would produce identities that
contradict themselves.

## The guard

Rules that live only in a generator get bypassed. Something else in the repository will
one day want a personal number in a fixture, a document or a test, and will reach for a
plausible one.

So the repository is scanned. Every build checks the whole working tree, not only
`fixtures/`, for anything shaped like a real CPR number: ten digits, with or without a
separator, forming a valid date in an unraised month-day. The scan decodes base64url
segments before looking, because a number inside a token payload is exactly as exposed
as one in plain text.

The scan was widened to the whole tree for a reason. The first plausible number it found
was not in a fixture at all — it was in a document, written while explaining how the
fixtures avoid them.

## Recordings

The fixtures under `fixtures/` are recordings of real exchanges with the broker's
pre-production environment, made while a person authenticated with a test identity. The
personal number, the subject, the session identifier and every token were replaced
before anything was written to disk.

Replacement is per value, not per occurrence: one real value becomes one placeholder
everywhere it appears. Whether the session identifier in one token equals the one in
another is a fact those recordings exist to establish, and a fresh pseudonym each time
would have destroyed it while looking careful.

Tokens are never rewritten in place. Changing a byte inside a signed token invalidates
its signature, and re-signing it produces bytes the broker never sent. The compact token
is replaced with a placeholder, and its decoded header and payload are written beside it
verbatim — the member order inside them is the whole evidence for what the broker sends.

## Real numbers

Nothing stops a caller supplying a real personal number to a StubID instance they run.
StubID does not generate one, does not log one, and sends none anywhere. If you point a
test at a real person's number, that is a decision you have made, and the number stays
on your machine.
