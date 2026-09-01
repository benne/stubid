#!/usr/bin/env bash
#
# Trusts StubID's certificate the way each browser engine requires, and runs the sign-in suite
# against all three. Written to be run inside mcr.microsoft.com/playwright, which is where the
# browsers and their system dependencies come from and where every trust install below is
# therefore thrown away with the container. Nothing here writes to the host's trust stores.
#
#   STUBID_AUTHORITY  the secured issuer, e.g. https://localhost:18443/op
#   STUBID_CONTROL    the plain-HTTP control API, e.g. http://localhost:18081
#   STUBID_CERT       the certificate, fetched from the control API before anything trusted it
#
# Every negative control runs before anything is installed, then the trust goes in, then every
# sign-in. Per-engine ordering would also work - Chromium and Firefox read NSS databases rather
# than the system bundle, so WebKit's update-ca-certificates cannot reach them - but that is
# cross-engine reasoning baked into a script, and one order needs none of it. The log reads as
# three refusals followed by three sign-ins.
set -euo pipefail

: "${STUBID_AUTHORITY:=https://localhost:18443/op}"
: "${STUBID_CONTROL:=http://localhost:18081}"
: "${STUBID_CERT:=/certs/stubid.crt}"

export STUBID_AUTHORITY STUBID_CONTROL

suite=${SUITE_DIR:-/suite}
work=${WORK_DIR:-/work}

# The suite is mounted read-only and built in a copy, so npm never writes node_modules into a
# bind-mounted working tree as root. That is how tests/interop-spring/target became root-owned.
mkdir -p "$work"
cp "$suite"/package.json "$suite"/signin.mjs "$work/"
cd "$work"

# certutil is what Chromium and Firefox trust is written with, and the Playwright image has no
# NSS tools.
apt-get update -qq >/dev/null
apt-get install -y -qq libnss3-tools >/dev/null

npm install --no-audit --no-fund --silent

untrusted=$work/firefox-untrusted
trusted=$work/firefox-trusted
mkdir -p "$untrusted" "$trusted"

echo
echo "== Nothing trusts the certificate yet =="

for engine in chromium firefox webkit; do
  PW_ENGINE=$engine STUBID_EXPECT=refused PW_FIREFOX_PROFILE=$untrusted node signin.mjs
done

echo
echo "== Trusting it, three different ways =="

# Chrome and Edge on Linux read an NSS database rather than the system bundle. P is a trusted
# peer for server authentication, which is what a self-signed leaf is; C, for a certificate
# authority, is refused here with a different error.
mkdir -p "$HOME/.pki/nssdb"
certutil -d "sql:$HOME/.pki/nssdb" -N --empty-password 2>/dev/null || true
certutil -d "sql:$HOME/.pki/nssdb" -A -n stubid -t "P,," -i "$STUBID_CERT"
echo "  chromium: $HOME/.pki/nssdb, as a trusted peer"

# Firefox reads its own profile, and wants the opposite flag: it takes the leaf as a trust anchor
# under C and refuses it under P. A seeded profile can only be used by launchPersistentContext.
certutil -d "sql:$trusted" -N --empty-password 2>/dev/null || true
certutil -d "sql:$trusted" -A -n stubid -t "C,," -i "$STUBID_CERT"
echo "  firefox:  a profile database, as a certificate authority"

# WebKit is the only one of the three that reads the operating system's bundle.
cp "$STUBID_CERT" /usr/local/share/ca-certificates/stubid.crt
update-ca-certificates >/dev/null
echo "  webkit:   /usr/local/share/ca-certificates"

echo
echo "== Signing in =="

for engine in chromium firefox webkit; do
  # The browser's trust is above; this is node's, for the back channel the client library runs.
  # A suite needs both, and setting only one is where everybody stops first.
  PW_ENGINE=$engine PW_FIREFOX_PROFILE=$trusted \
    NODE_EXTRA_CA_CERTS=$STUBID_CERT node signin.mjs
done

echo
echo "Three engines drove a login against StubID, with nothing relaxed."
