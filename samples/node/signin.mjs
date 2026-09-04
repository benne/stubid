// A real sign-in with openid-client: a starting point to copy from, and a check that runs in
// CI on every change. The assertions below are the half a sample would not have - they are
// what stops this file quietly ceasing to work, which would be worse than not having it.
//
// It checks things the .NET handler does not.
//
// Two in particular. It asserts that the issuer it discovers equals the address it was given,
// character for character - and StubID's issuer carries a path segment, which is exactly the
// shape that trips that assertion. And once metadata advertises the iss authorization-response
// parameter, it refuses a response that omits it.
//
// It also runs twice in CI, against two instances. Over plain HTTP it is given openid-client's
// http allowance; over https it is given nothing at all, and the handshake works because node was
// started with StubID's certificate in NODE_EXTRA_CA_CERTS. Dropping the allowance is the whole of
// that second proof, so it is decided from the authority's scheme rather than from a flag somebody
// could forget to pass.
import * as client from 'openid-client'

const authority = process.env.STUBID_AUTHORITY ?? 'http://localhost:8080/op'
const clientId = '0a775a87-878c-4b83-abe3-ee29c720c3e7'
const redirectUri = 'http://localhost:5099/callback'

const secured = new URL(authority).protocol === 'https:'

// One expression, so there is exactly one place a relaxation could come back.
const relaxations = secured ? undefined : { execute: [client.allowInsecureRequests] }

function ok(what) {
  console.log(`  ${what}`)
}

console.log(secured
  ? `https, and openid-client is given no allowance: ${authority}`
  : `http, with client.allowInsecureRequests: ${authority}`)

const config = await client.discovery(
  new URL(authority),
  clientId,
  'the-secret-the-existing-configuration-carries',
  undefined,
  relaxations,
)
ok(`discovery resolved and the issuer matched the configured authority: ${authority}`)

const verifier = client.randomPKCECodeVerifier()
const challenge = await client.calculatePKCECodeChallenge(verifier)
const state = client.randomState()
const nonce = client.randomNonce()

const authorizationUrl = client.buildAuthorizationUrl(config, {
  redirect_uri: redirectUri,
  scope: 'openid mitid',
  code_challenge: challenge,
  code_challenge_method: 'S256',
  state,
  nonce,
})

// The browser's part: follow the authorize request and read where it sends the user.
const authorized = await fetch(authorizationUrl, { redirect: 'manual' })
const location = authorized.headers.get('location')
if (!location) {
  throw new Error(`authorize did not redirect; it answered ${authorized.status}`)
}
ok('authorize redirected back to the client')

// The library validates the whole exchange: state, PKCE, the issuer, the signature against the
// published keys, the nonce, and the iss parameter.
const tokens = await client.authorizationCodeGrant(config, new URL(location), {
  pkceCodeVerifier: verifier,
  expectedNonce: nonce,
  expectedState: state,
})
ok('the token exchange was accepted, signature and nonce included')

const claims = tokens.claims()
if (!claims.iss.endsWith('/op')) {
  throw new Error(`the issuer lost its path segment: ${claims.iss}`)
}
ok(`id_token issuer kept its path segment: ${claims.iss}`)

const userinfo = await client.fetchUserInfo(config, tokens.access_token, claims.sub)
if (typeof userinfo['mitid.age'] !== 'string') {
  throw new Error('mitid.age came back as something other than a string')
}
ok('userinfo returned, and its values are strings as the broker sends them')

console.log('openid-client accepted StubID')
