// A real browser drives StubID's front channel, and a stock client library finishes the login.
//
// One engine per invocation, named by PW_ENGINE, because each of the three trusts a certificate
// by a different mechanism and a failure should name the engine rather than the matrix.
//
// There is no ignoreHTTPSErrors in this file, and that absence is the whole claim: every engine
// reaches the instance because something trusted the certificate it serves, not because the
// browser was told to stop checking. STUBID_EXPECT=refused runs the same launch with none of
// that trust in place and requires the handshake to fail, which is what keeps the claim from
// being vacuous.
//
// What the browser adds over the Node, Java and .NET stacks is two things they cannot do: it
// makes the trust decision in its own TLS stack rather than the platform's, and it runs the
// form_post page's auto-submit, which needs a JavaScript engine.
import * as client from 'openid-client'
import { chromium, firefox, webkit } from 'playwright'

const authority = process.env.STUBID_AUTHORITY ?? 'https://localhost:18443/op'
const control = process.env.STUBID_CONTROL ?? 'http://localhost:18081'
const engine = process.env.PW_ENGINE ?? 'chromium'
const expectation = process.env.STUBID_EXPECT ?? 'trusted'
const profile = process.env.PW_FIREFOX_PROFILE

const clientId = '0a775a87-878c-4b83-abe3-ee29c720c3e7'
const secret = 'the-secret-the-existing-configuration-carries'

// Nothing listens here and nothing needs to. The browser's request is read as it is issued, so
// the callback is an address rather than a server. https, not the http the recordings use: a
// form submitted from an https page to an http action is a browser policy that differs by
// engine and by version, and this suite has no reason to depend on it.
const redirectUri = 'https://localhost:5099/callback'

// The suite exists to prove a browser needs nothing relaxed to reach a secured instance, so it
// refuses to run against an address where there would be nothing to prove.
if (new URL(authority).protocol !== 'https:') {
  throw new Error(`this suite runs over TLS only, and was given ${authority}`)
}

const engines = { chromium, firefox, webkit }

// The certificate error each engine reports when nothing trusts what the listener presented.
// Asserting the engine's own words rather than "navigation failed" is what stops the negative
// control passing because the container was down.
const refusals = {
  chromium: 'ERR_CERT_AUTHORITY_INVALID',
  firefox: 'SSL_ERROR_UNKNOWN',
  webkit: 'TLS certificate',
}

const first = (message) => message.split('\n')[0]

function ok(what) {
  console.log(`  ${what}`)
}

/**
 * Firefox trusts a certificate only from a profile directory seeded with certutil, and a seeded
 * profile can only be handed to launchPersistentContext. That is a constraint on the suite
 * rather than a line of setup: one context per launch, so no browser.newContext() isolation.
 */
async function open() {
  if (engine === 'firefox') {
    const context = await firefox.launchPersistentContext(profile, {})
    return { context, close: () => context.close() }
  }

  const browser = await engines[engine].launch()
  const context = await browser.newContext()

  return { context, close: async () => { await context.close(); await browser.close() } }
}

/**
 * Walks an authorization URL in the browser and returns what it sent to the callback.
 *
 * Listening rather than intercepting, which is not a preference. A page.route handler never
 * fires for a redirect hop, so it catches the form_post submission - a fresh navigation the
 * page's own script starts - and silently misses the ordinary one. Measured in all three
 * engines before this was written.
 */
async function walk(context, authorizationUrl) {
  const page = await context.newPage()
  const callback = page.waitForRequest(r => r.url().startsWith(redirectUri), { timeout: 20000 })

  // Nothing answers on the callback address, so this navigation is expected to end in a
  // connection error. The assertion is on the request the browser made, never on the
  // navigation, which is why the failure is kept rather than thrown: if the callback is never
  // reached it says why.
  let navigation = null
  await page.goto(authorizationUrl.href, { waitUntil: 'domcontentloaded', timeout: 30000 })
    .catch((failure) => { navigation = failure })

  const request = await callback.catch(() => {
    throw new Error(navigation
      ? `the browser never reached the callback: ${first(navigation.message)}`
      : 'the browser never reached the callback');
  })

  await page.close()

  return request.method() === 'POST'
    ? { method: 'POST', body: request.postData() ?? '', values: new URLSearchParams(request.postData() ?? '') }
    : { method: 'GET', url: request.url(), values: new URL(request.url()).searchParams }
}

/** Queues an outcome for the next login. Plain HTTP: the control API answers there even on a secured instance. */
async function enqueue(decision) {
  const response = await fetch(`${control}/_stubid/v1/behaviours/enqueue`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(decision),
  })

  if (!response.ok) {
    throw new Error(`enqueue answered ${response.status}`)
  }
}

function authorizationUrl(config, checks, extra = {}) {
  return client.buildAuthorizationUrl(config, {
    redirect_uri: redirectUri,
    scope: 'openid mitid',
    code_challenge: checks.challenge,
    code_challenge_method: 'S256',
    state: checks.state,
    nonce: checks.nonce,
    ...extra,
  })
}

async function checks() {
  const verifier = client.randomPKCECodeVerifier()

  return {
    verifier,
    challenge: await client.calculatePKCECodeChallenge(verifier),
    state: client.randomState(),
    nonce: client.randomNonce(),
  }
}

const { context, close } = await open()

try {
  if (expectation === 'refused') {
    // No discovery first: openid-client would fail on node's own trust, which is a different
    // claim. This is the browser's TLS stack and nothing else.
    const page = await context.newPage()
    const reached = await page
      .goto(`${authority}/.well-known/openid-configuration`, { timeout: 30000 })
      .then(() => null, (failure) => first(failure.message))

    if (reached === null) {
      throw new Error(`${engine} completed the handshake with nothing trusting the certificate`);
    }

    if (!reached.includes(refusals[engine])) {
      throw new Error(`${engine} refused, but not over the certificate: ${reached}`)
    }

    console.log(`${engine} refuses the certificate until something trusts it`)
    ok(reached)
  } else {
    console.log(`${engine}, against ${authority}, with nothing relaxed`)

    // Node's own trust, for the back channel. The browser's store covers none of this, which is
    // the halfway state everyone lands in first.
    const config = await client.discovery(new URL(authority), clientId, secret)
    ok('discovery resolved over TLS, and the issuer matched the configured authority')

    // The ordinary path: the browser follows the redirect and the library redeems what it carried.
    {
      const check = await checks()
      const callback = await walk(context, authorizationUrl(config, check))

      if (callback.method !== 'GET') {
        throw new Error(`query mode arrived as ${callback.method}`)
      }

      const tokens = await client.authorizationCodeGrant(config, new URL(callback.url), {
        pkceCodeVerifier: check.verifier,
        expectedNonce: check.nonce,
        expectedState: check.state,
      })

      const claims = tokens.claims()
      if (!claims.iss.endsWith('/op')) {
        throw new Error(`the issuer lost its path segment: ${claims.iss}`)
      }

      ok(`the browser carried a code back, and the exchange was accepted: ${claims.iss}`)
    }

    // The one thing no other stack in this repository can check. StubID answers a form_post
    // request with a page whose body carries onload="document.forms[0].submit()", and every
    // other suite reads the hidden inputs out of the HTML and posts them by hand, because an
    // HTTP client has no JavaScript engine to run it with.
    {
      const check = await checks()
      const callback = await walk(
        context, authorizationUrl(config, check, { response_mode: 'form_post' }))

      if (callback.method !== 'POST') {
        throw new Error(`the form_post page did not submit itself; the callback arrived as ${callback.method}`)
      }

      const tokens = await client.authorizationCodeGrant(
        config,
        new Request(redirectUri, {
          method: 'POST',
          headers: { 'content-type': 'application/x-www-form-urlencoded' },
          body: callback.body,
        }),
        {
          pkceCodeVerifier: check.verifier,
          expectedNonce: check.nonce,
          expectedState: check.state,
        })

      ok(`the form_post page submitted itself, and its code was accepted: ${tokens.claims().sub}`)
    }

    // A refusal, which is the outcome a suite most wants and which no broker add-on will produce
    // on demand. Queued immediately before the navigation that consumes it: this instance is
    // shared, and a queued decision that outlived its own scenario would be spent by the next.
    {
      const check = await checks()
      await enqueue({ approve: false, clientId, errorCode: 'mitid_user_aborted' })

      const callback = await walk(context, authorizationUrl(config, check))
      const error = callback.values.get('error')
      const description = callback.values.get('error_description')

      if (error !== 'access_denied' || description !== 'mitid_user_aborted') {
        throw new Error(`a queued refusal came back as ${error}/${description}`)
      }

      ok(`a queued refusal reached the browser as ${error}, ${description}`)
    }

    console.log(`${engine} signed in against StubID`)
  }
} finally {
  await close()
}
