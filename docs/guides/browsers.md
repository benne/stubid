# Driving StubID from a browser test

The reason this project exists is that you cannot automate a MitID login: the widget is a
cross-origin iframe that detects and blocks browser automation, and every login in the broker's
pre-production environment has to be approved by hand. StubID is where that stops being true. A
Playwright test walks the whole front channel — the authorize redirect, the response, the callback
— and a real browser does the walking.

```
tests/interop-browser/matrix.sh
```

That script is the whole recipe, and it runs in CI on every change against the image the same job
built: three engines, each refused first and then trusted by its own mechanism, each signing in
over TLS with nothing relaxed. A copied example that has quietly stopped working is worse than no
example, so what follows is that script explained rather than a second version of it.

## Clicking through works; queueing is usually still better

A parked login redirects the browser to `/op/Login`, and driving it does what it looks like it
does: pick a citizen, click Approve, and the browser is carried back to the client with a code,
exactly as an automatically approved login is. Aborting returns
`error=access_denied&error_description=mitid_user_aborted`. A decision made through the control
API while the browser sits on that page is collected the same way, on its next navigation.

That was not always true — a parked login could not be resumed at all, and every guide here told
you to queue the outcome instead. It is true now, and a browser test that wants to exercise the
page should.

For everything else, queue the outcome anyway. It is one request rather than three, it needs no
page to drive and no second navigation, and it is the only way to get an outcome a person cannot
produce by clicking. So a browser test either leaves automatic approval on, or queues first:

```
POST /_stubid/v1/behaviours/enqueue
{ "approve": false, "clientId": "…", "errorCode": "mitid_user_aborted" }
```

and then navigates. The next matching login consumes it, and the browser is redirected back to
the client carrying `error=access_denied&error_description=mitid_user_aborted` — which is the
outcome a suite most wants to test and the one no broker add-on will produce on demand.

Queue it immediately before the navigation that consumes it. A queued decision outliving its own
test is spent by the next one, and that failure surfaces somewhere else entirely. [Deciding what a
login does](approvals.md) has the rest of the ladder.

## Two trusts, not one

This is the step everybody gets half right.

A browser test has two halves and they read different trust stores. The **browser** walks the
front channel and makes its own TLS decision in its own process. The **client library** — running
in Node, beside the test rather than inside the browser — discovers the metadata and redeems the
code, and it uses Node's trust.

```
NODE_EXTRA_CA_CERTS=/path/to/stubid.crt   # the library's half
certutil … / update-ca-certificates       # the browser's half, and it differs per engine
```

Setting only the first is the usual outcome, because it is the answer Playwright's own
documentation gives for certificates — and it is the right answer for Playwright's `request`
fixture and for its browser downloads, neither of which is the browser's TLS stack. The
sign-in then fails at the navigation with a certificate error while discovery has already
succeeded, which reads like a StubID problem and is not one.

## Trusting the certificate, per engine

Fetch it first. This needs no trust, which is the point of it — the control API answers on plain
HTTP even on a secured instance:

```
curl -fsS http://localhost:18081/_stubid/v1/runtime/tls-certificate.pem -o stubid.crt
```

Then, per engine. All three are measured, each against a control that must be refused first:

| Engine | Reads | Flag |
| --- | --- | --- |
| Chromium | an NSS database at `~/.pki/nssdb` | `P,,` |
| Firefox | its own profile database | `C,,` |
| WebKit | the operating system's bundle | — |

```
# Chromium
certutil -d sql:$HOME/.pki/nssdb -A -n stubid -t "P,," -i stubid.crt

# Firefox, into a profile directory you then launch with
certutil -d sql:/path/to/profile -A -n stubid -t "C,," -i stubid.crt

# WebKit
sudo cp stubid.crt /usr/local/share/ca-certificates/stubid.crt && sudo update-ca-certificates
```

**Chromium and Firefox want opposite flags**, which is the detail that costs an afternoon. `P` is
a trusted peer for server authentication and `C` is a certificate authority; StubID's certificate
is a self-signed leaf with `CA:FALSE`, so `P` is what it is. Firefox takes it as a trust anchor
under `C` anyway and refuses it under `P`. Chromium refuses `C` — with
`net::ERR_CERT_INVALID` rather than `net::ERR_CERT_AUTHORITY_INVALID`, so the wrong flag and no
flag at all are distinguishable, and worth reading carefully before you conclude the certificate
never arrived.

Chromium also accepts `--ignore-certificate-errors-spki-list=<base64 sha256 of the DER SPKI>`,
which installs nothing and is bounded to one key. It is honestly an ignore-errors flag, though:
it also waves through an expired certificate and a name that does not match, for that key.

Firefox's `policies.json` `Certificates.Install` does **not** work here. It is the documented
enterprise mechanism and it takes no effect on a Playwright-launched Firefox at all.

## Firefox forces `launchPersistentContext`

Not a line of setup — a constraint on the suite. A seeded profile can only be given to
`launchPersistentContext`, so under Firefox there is one context per launch and no
`browser.newContext()` isolation between tests:

```js
const context = await firefox.launchPersistentContext(profile, {})
```

Chromium and WebKit are unconstrained, because their trust is outside the profile. If your suite
is written around per-test contexts, Firefox is the engine that will make you restructure it, and
it is better to know that before you write the other two.

## The callback needs no relying party

Point the redirect URI at an address nothing serves, and read the request as the browser issues
it:

```js
const callback = page.waitForRequest(r => r.url().startsWith(redirectUri))

// Nothing answers there, so this navigation is expected to fail. The assertion is on the
// request, never on the navigation.
await page.goto(authorizationUrl, { waitUntil: 'domcontentloaded' }).catch(() => {})

const request = await callback
```

**Do not reach for `page.route` here.** It looks like the right tool and it is quietly the wrong
one: a route handler never fires for a redirect hop. It catches the `form_post` submission, which
is a fresh navigation the page's own script starts, and misses the ordinary redirect — so a suite
built on it passes one scenario, fails the other, and gives no hint why. Measured in all three
engines.

Use an `https` redirect URI even though nothing is listening. A form submitted from an https page
to an http action is a browser policy that has changed more than once and differs between engines,
and there is no reason to depend on it. StubID validates only that a redirect URI is present, and
that the token request repeats the same one.

## `form_post` needs a JavaScript engine

StubID answers a `response_mode=form_post` request the way the broker does: a page whose body
carries `onload="document.forms[0].submit()"`. An HTTP client cannot run that — every other test
in this repository reads the hidden inputs out of the HTML and posts them by hand. A browser is
the only thing that executes the page as written, which makes this the scenario a browser test is
uniquely worth writing.

The callback then arrives as a `POST`, so read `request.postData()` rather than the URL.

## What trusting it costs you locally

`~/.pki/nssdb` is your own Chrome and Edge trust store, not a test fixture. What you add there,
you added to your browser:

```
certutil -d sql:$HOME/.pki/nssdb -L              # what is in there
certutil -d sql:$HOME/.pki/nssdb -D -n stubid    # take it out again
```

The same is true of the system bundle for WebKit. The private key for that certificate sits in
the key directory under a password that is a constant in this project's source, so anyone who can
read that directory can present a certificate your machine now accepts for `localhost`. Not on a
shared machine, and not with a key directory you did not create. [Trusting the certificate StubID
serves](certificates.md) has the rest of that argument.

Running the browsers in a container avoids the question entirely, which is what CI does: the
trust installs land in the container's own writable layer and go when it does, and the host's
stores are never touched.

## Or trust nothing

Plain HTTP is the default and the control API uses it even on a secured instance. Leave
`StubId__Tls` unset and there is no certificate, nothing to install, and none of this guide
applies — the front channel, the `form_post` auto-submit and the queued outcomes all work exactly
the same. TLS is worth turning on when the application under test insists on it, and not before.

## What this does not cover

The matrix runs on Linux, in Playwright's own image. **WebKit on Linux approximates Safari; it is
not Safari.** It shares the engine and not the platform's TLS stack or its trust store, so a
WebKit pass is evidence about the engine rather than about a Mac.

Windows and macOS trust stores are in [the certificate guide](certificates.md) and are documented
rather than run — there is no macOS runner here. The mechanisms above are the engines' own rather
than Playwright's, so Selenium, Cypress and WebdriverIO need the same three trust steps; what
changes is only how you launch and how you read the callback.
