#!/usr/bin/env bash
# Verifies the published image the way an adopter meets it: a real login through the
# container, and a restart that a client's cached metadata survives.
set -euo pipefail

IMAGE="${1:-stubid:test}"
PORT="${PORT:-18080}"
NAME="stubid-verify-$$"
VOLUME="stubid-verify-keys-$$"
BASE="http://localhost:${PORT}"
CLIENT=0a775a87-878c-4b83-abe3-ee29c720c3e7
REDIRECT=http://localhost:5099/callback

cleanup() {
  docker rm -f "$NAME" >/dev/null 2>&1 || true
  docker volume rm "$VOLUME" >/dev/null 2>&1 || true
}
trap cleanup EXIT

start() {
  docker run -d --name "$NAME" -p "${PORT}:8080" -v "${VOLUME}:/keys" \
    -e "StubId__PublicBaseUrl=${BASE}" "$IMAGE" >/dev/null
  for _ in $(seq 1 40); do
    if curl -fsS -o /dev/null "${BASE}/op/.well-known/openid-configuration" 2>/dev/null; then return; fi
    sleep 0.5
  done
  echo "the container never became ready"; docker logs "$NAME"; exit 1
}

# Fails rather than passing on an empty answer, which is how a check like this quietly stops
# checking anything.
kids() {
  local out
  out=$(curl -fsS "${BASE}/op/.well-known/openid-configuration/jwks" \
        | python3 -c 'import json,sys; print(",".join(k["kid"] for k in json.load(sys.stdin)["keys"]))')
  [ -n "$out" ] || { echo "the key set was empty"; exit 1; }
  echo "$out"
}

start
echo "container is up"

issuer=$(curl -fsS "${BASE}/op/.well-known/openid-configuration" \
         | python3 -c 'import json,sys; print(json.load(sys.stdin)["issuer"])')
[ "$issuer" = "${BASE}/op" ] || { echo "issuer is ${issuer}, expected ${BASE}/op"; exit 1; }
echo "issuer matches the address a client would be configured with: ${issuer}"

# A whole login, back channel included.
location=$(curl -fsS -o /dev/null -D - \
  "${BASE}/op/connect/authorize?client_id=${CLIENT}&response_type=code&redirect_uri=$(python3 -c 'import urllib.parse,sys;print(urllib.parse.quote(sys.argv[1]))' "$REDIRECT")&scope=openid%20mitid&state=s&nonce=n" \
  | grep -i '^location:' | tr -d '\r' | sed 's|location: ||')

code=$(python3 -c 'import sys,urllib.parse as u; print(u.parse_qs(u.urlparse(sys.argv[1]).query)["code"][0])' "$location")
tokens=$(curl -fsS -X POST "${BASE}/op/connect/token" \
  -d grant_type=authorization_code -d "code=${code}" -d "redirect_uri=${REDIRECT}" \
  -d "client_id=${CLIENT}" -d client_secret=any)

python3 - "$tokens" <<'PY'
import json, sys, base64
body = json.loads(sys.argv[1])
for member in ("id_token", "access_token", "token_type", "scope"):
    assert member in body, f"the token response is missing {member}"
payload = body["id_token"].split(".")[1]
claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
assert claims["iss"].endswith("/op"), claims["iss"]
print("a full login completed through the container")
PY

before=$(kids)
docker restart "$NAME" >/dev/null
for _ in $(seq 1 40); do
  curl -fsS -o /dev/null "${BASE}/op/.well-known/openid-configuration" 2>/dev/null && break
  sleep 0.5
done
after=$(kids)

if [ "$before" != "$after" ]; then
  echo "the signing keys changed across a restart."
  echo "  before: ${before}"
  echo "  after:  ${after}"
  echo "Every client that cached the metadata would fail with a key-resolution error until"
  echo "its cache expired, with nothing on its side to explain why."
  exit 1
fi

echo "the signing keys survived a restart, so a cached metadata document stays valid"
echo "all container checks passed"
