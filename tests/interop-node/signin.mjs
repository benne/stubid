// A real sign-in with openid-client, which checks things the .NET handler does not.
//
// Two in particular. It asserts that the issuer it discovers equals the address it was given,
// character for character - and StubID's issuer carries a path segment, which is exactly the
// shape that trips that assertion. And once metadata advertises the iss authorization-response
// parameter, it refuses a response that omits it.
import * as client from 'openid-client'

const authority = process.env.STUBID_AUTHORITY ?? 'http://localhost:18080/op'
const clientId = '0a775a87-878c-4b83-abe3-ee29c720c3e7'
const redirectUri = 'http://localhost:5099/callback'

function ok(what) {
  console.log(`  ${what}`)
}

const config = await client.discovery(
  new URL(authority),
  clientId,
  'the-secret-the-existing-configuration-carries',
  undefined,
  { execute: [client.allowInsecureRequests] },
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
