# Trademarks

MitID is a registered trademark of Digitaliseringsstyrelsen. Nets eID Broker and
Signaturgruppen are marks of Signaturgruppen A/S. Idura and Criipto are marks of their
owner. NemLog-in is a service of Digitaliseringsstyrelsen. All are used here only to state
factually which systems StubID emulates. No endorsement is claimed or implied.

The Apache-2.0 licence covering this project grants no trademark rights (see section 6 of
the licence).

## What this project will not do

StubID emulates protocol surfaces. It does not reproduce anyone's brand:

- No MitID logo, icons, colours, typefaces, or design components.
- No copy of the MitID authenticator interface. The stub login page is plainly StubID's own
  and says so on the page.
- No domain containing `mitid`, and no `*.mitid.dk` subdomain.
- No wording that suggests a real authentication took place, or that StubID is certified,
  approved, or connected to any broker.

Every response carries an `X-StubID-Emulator` header so an instance cannot be mistaken for
a production system.

## Documentation and error text

Protocol identifiers are functional facts and are reproduced exactly, because a test that
asserts on `mitid_user_aborted` needs that exact string. Vendor documentation prose is not
reproduced: the broker reference under `docs/brokers/` is written from observed behaviour
and from recorded exchanges, not copied from anyone's documentation.
