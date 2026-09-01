# Trusting the certificate StubID serves

StubID serves TLS when you ask it to, with a certificate it generates for itself. Nothing on your
machine trusts that certificate yet, and StubID will not install it anywhere — so trusting it is
something you do, per process or per machine, with the file it hands you.

```
docker run -d -p 18080:8080 -p 18443:8443 -v stubid-keys:/keys \
  -e StubId__Tls=self-signed \
  -e StubId__PublicBaseUrl=https://localhost:18443 ghcr.io/benne/stubid

curl -fsS http://localhost:18080/_stubid/v1/runtime/tls-certificate.pem -o stubid.crt
curl -fsS --cacert stubid.crt https://localhost:18443/op/.well-known/openid-configuration
```

Those last two lines are the `Take the certificate the way a client stack would` step of the
`interop` job in [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml), which runs on every
change against the image the same job built. It uses port 18081 rather than 18080 for the control
port, because it already has a plain instance on 18080, and writes the file into the runner's
temporary directory; between the two it runs an `openssl x509` line that prints what was fetched
into the build log. A copied example that has quietly stopped working is worse than no example.

The first command needs no trust, which is the point of it. The control API answers on plain HTTP
even when the instance is serving TLS, so the certificate can be fetched before anything has been
given the means to decide whether to trust it. The second command is the whole claim: full
validation, no flag turned off, nothing waved through.

## What you just trusted

One certificate, and only for the names in it.

```
openssl x509 -in stubid.crt -noout -subject -dates -ext subjectAltName,basicConstraints
```

It is a leaf, not a certificate authority: basic constraints say `CA:FALSE`, and the names are
`localhost`, `127.0.0.1`, `::1` and the container's own hostname. Add more with
`StubId__Tls__SubjectAlternativeNames=stubid,stubid.internal`.

It is not a certificate authority on purpose. A CA would be more convenient — trust it once and
every instance you ever run is covered — and it would also be a machine-wide impersonation kit,
because its private key would sit in the key directory under a password that is a constant in this
project's source. Anyone who could read that directory could then mint a certificate for any name
at all. A leaf can only ever be the handful of names written into it.

## Node

```
NODE_EXTRA_CA_CERTS=/path/to/stubid.crt node your-tests.js
```

That adds StubID's certificate to the roots Node already trusts rather than replacing them. It is
read once, when the process starts, so exporting it afterwards does nothing — it has to be on the
command that launches Node. It is also ignored for any request that passes an explicit `ca` option.

`tests/interop-node/signin.mjs` drives a complete sign-in this way in CI. Over https it passes
`openid-client` no allowance at all — it decides that from the scheme of the authority it was given,
so the secured run cannot carry a relaxation somebody forgot to remove.

## Java

A JVM reads no operating-system trust store. Trusting the certificate in Windows or in
`/usr/local/share/ca-certificates` changes nothing for a Java process, so this is a file.

```
cp "$JAVA_HOME/lib/security/cacerts" stubid-truststore.p12
keytool -importcert -noprompt -alias stubid -file stubid.crt \
  -keystore stubid-truststore.p12 -storepass changeit
```

```
-Djavax.net.ssl.trustStore=/path/to/stubid-truststore.p12 \
-Djavax.net.ssl.trustStorePassword=changeit
```

**Copy the JDK's own `cacerts` and add to it.** `javax.net.ssl.trustStore` replaces the default
trust rather than extending it, so a store holding only StubID's certificate is a JVM that trusts
nothing else — no Maven Central, no artifact repository, no other service the same process talks to.
That failure arrives as a download error rather than as a trust error, and it is not obvious.
`changeit` is the password `cacerts` already has.

Under Maven, `exec:java` runs the class in Maven's own JVM, so the two properties go in `MAVEN_OPTS`
rather than on the command line.

## curl, and anything else built on OpenSSL

```
curl --cacert stubid.crt https://localhost:18443/op/.well-known/openid-configuration
```

Per invocation, which is what you want. `SSL_CERT_FILE` would do it for a whole process tree, and it
replaces the system bundle while it is set — the same trap as the JVM's truststore, with a wider
blast radius.

## .NET

A .NET test suite usually needs none of this. [`StubId.Testing`](testcontainers.md) reads the
certificate over the control API while the container starts, and `CreateTrustingHandler()` returns a
handler that trusts that exact certificate and builds no chain at all.

When a suite has to hand the file to something it spawns — a Node process, a browser — it already
holds the certificate and can write it out:

```csharp
await File.WriteAllTextAsync(path, stub.ServerCertificate!.ExportCertificatePem());
```

## Your operating system, and your browser

These are the recipes for a browser you drive by hand. Three of them are also what a browser
test installs, and CI runs those three on every change — Ubuntu's `update-ca-certificates`, the
NSS entry below, and Firefox's profile — through [a browser test](browsers.md). The Fedora,
Windows and macOS steps are documented rather than run: the job with a running instance in it is
Linux only.

Debian and Ubuntu:

```
sudo cp stubid.crt /usr/local/share/ca-certificates/stubid.crt
sudo update-ca-certificates
```

Fedora and RHEL:

```
sudo cp stubid.crt /etc/pki/ca-trust/source/anchors/stubid.crt
sudo update-ca-trust
```

Chrome and Edge on Linux read NSS rather than the system bundle, so they need their own entry:

```
certutil -d sql:$HOME/.pki/nssdb -A -n stubid -t "P,," -i stubid.crt
```

`P` means trusted peer for server authentication, as opposed to `C` for a certificate authority.
That is the right flag for a self-signed leaf, and it is what Microsoft's own documentation
prescribes for the ASP.NET Core development certificate, which has the same shape. Chromium
refuses `C` for this certificate, and says so with `net::ERR_CERT_INVALID` rather than the
`net::ERR_CERT_AUTHORITY_INVALID` it gives for one it has never seen — so the wrong flag is
distinguishable from no flag, if you read the error rather than the outcome.

On Windows, `certutil -addstore -user Root stubid.crt` puts it where Chrome and Edge look. On macOS,
`security add-trusted-cert -d -r trustRoot -k ~/Library/Keychains/login.keychain-db stubid.crt`
does the same for Safari and Chrome.

Firefox reads no operating-system store on Linux or macOS, and no NSS database but its own
profile's. On Windows, `security.enterprise_roots.enabled` makes it read one. For a browser you
are driving by hand, the path that certainly works is the exception Firefox offers on the first
visit.

For one you are automating, seed the profile before launching it — and with `C,,`, the flag
Chromium refuses:

```
certutil -d sql:/path/to/profile -A -n stubid -t "C,," -i stubid.crt
```

Firefox takes a `CA:FALSE` leaf as a trust anchor under that flag and refuses it under `P,,`,
which is the opposite of Chromium in both directions. Its `policies.json` `Certificates.Install`
is the documented enterprise mechanism and takes no effect at all on a Playwright-launched
Firefox; this guide said that was untested, and it has now been measured. [Driving StubID from a
browser test](browsers.md) has the rest.

## When the certificate changes

It is written to the key directory on first use and read afterwards, for the same reason the signing
keys are, so a restart with the same volume presents the same certificate and everything you trusted
keeps working.

A fresh volume, a different `StubId__KeyPath`, or a container with no volume at all means a new
certificate and every trust you installed silently stops matching. Compare what you have against
what the instance is serving:

```
openssl x509 -in stubid.crt -noout -fingerprint -sha1
curl -fsS http://localhost:18080/_stubid/v1/runtime/tls-certificate
```

The `thumbprint` in that JSON is the same value.

Adding a name to `StubId__Tls__SubjectAlternativeNames` after the certificate exists does not
regenerate it. Delete `tls.pfx` from the key directory, or start with a fresh one.

## What trusting it costs

Anyone who can read the key directory holds the private key for every name in that certificate, and
the file's password is a constant in this project's source. Trusting it in your operating system or
your browser therefore means that anyone who can read that directory can present a certificate your
machine accepts for `localhost`.

So: not on a shared machine, and not with a key directory you did not create. Prefer the per-process
recipes — `--cacert`, `NODE_EXTRA_CA_CERTS`, a truststore file — each of which is bounded by the
process that read it and goes away with it.

## If you would rather trust nothing

Plain HTTP is the default, and it is what the control API uses even on a secured instance. Leave
`StubId__Tls` unset and there is no certificate and nothing to decide.

`StubId__Tls=pkcs12` with `StubId__Tls__Path` — or `WithTlsCertificate` from the module — serves a
certificate you supply, which is the answer when the environment already trusts something. Running
StubID from a test suite is [its own guide](testcontainers.md); running it inside the test process,
where there is no listener and so nothing to trust, is [another](in-process.md).
