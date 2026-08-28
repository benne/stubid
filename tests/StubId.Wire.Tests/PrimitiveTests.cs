using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace StubId.Wire.Tests;

public class PkceTests
{
    [Fact]
    public void S256_accepts_the_verifier_behind_the_challenge()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var challenge = Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        Assert.True(Pkce.Verify(verifier, challenge, "S256"));
        Assert.False(Pkce.Verify("a-different-verifier", challenge, "S256"));
    }

    [Fact]
    public void Plain_is_accepted_because_the_broker_advertises_it()
    {
        // Weaker, but refusing it would mean rejecting a request the real broker accepts,
        // which is the failure mode this project exists to avoid.
        Assert.True(Pkce.Verify("the-verifier", "the-verifier", "plain"));
        Assert.False(Pkce.Verify("the-verifier", "something-else", "plain"));
    }

    [Fact]
    public void An_unknown_method_is_refused_rather_than_assumed()
    {
        Assert.False(Pkce.Verify("v", "v", "S512"));
        Assert.False(Pkce.Verify("v", "v", ""));
    }
}

public class HashClaimTests
{
    [Fact]
    public void The_hash_is_the_left_half_of_the_digest()
    {
        // Taking the whole digest is the easy mistake, and it surfaces to a client as a
        // rejected token rather than as anything that points here.
        var value = "an-authorization-code";
        var full = SHA256.HashData(Encoding.ASCII.GetBytes(value));

        Assert.Equal(Base64Url.Encode(full.AsSpan(0, 16)), HashClaims.Compute(value));
        Assert.Equal(16, Base64Url.Decode(HashClaims.Compute(value)).Length);
    }

    [Theory]
    // OpenID Connect Core, appendix A.3 (at_hash) and A.4 (c_hash). Both computed
    // independently before being pinned here.
    [InlineData("jHkWEdUXMU1BwAsC4vtUsZwnNvTIxEl0z9K3vx5KF0Y", "77QmUPtjPfzWtF2AnpK9RQ")]
    [InlineData("Qcb0Orv1zh30vL1MPRsbm-diHiMwcLyZvn1arpZv-Jxf_11jnpEX3Tgfvk", "LDktKdoQak3Pk0cnXxCltA")]
    public void The_examples_from_the_specification_match(string value, string expected)
    {
        Assert.Equal(expected, HashClaims.Compute(value));
    }
}

public class Uuid5Tests
{
    [Fact]
    public void The_example_from_the_specification_matches()
    {
        // RFC 9562, appendix A.4: the DNS namespace and the name "www.example.com".
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        Assert.Equal(
            new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"),
            Uuid5.Create(dns, "www.example.com"));
    }

    [Fact]
    public void The_same_name_always_gives_the_same_identifier()
    {
        // This is what lets a subject identifier survive a restart instead of being
        // regenerated into every client's cache.
        var ns = new Guid("11111111-2222-3333-4444-555555555555");

        Assert.Equal(Uuid5.Create(ns, "org-a|citizen-1"), Uuid5.Create(ns, "org-a|citizen-1"));
        Assert.NotEqual(Uuid5.Create(ns, "org-a|citizen-1"), Uuid5.Create(ns, "org-b|citizen-1"));
    }
}

public class KeyRingTests
{
    [Fact]
    public void Keys_load_from_pkcs12_quickly_enough_to_boot_behind()
    {
        // Generating RSA keys at startup is the slowest thing a container can do, so the
        // deployment path loads them. Loading is timed because "load, don't generate" is only
        // worth saying if the loading is actually fast.
        var material = TestKeys.Keys.Keys
            .Select(k => (k.Certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, "pw"), (string?)"pw", k.Use))
            .ToList();

        var stopwatch = Stopwatch.StartNew();
        using var loaded = KeyRing.Load(material);
        stopwatch.Stop();

        Assert.Equal(TestKeys.Keys.Keys.Count, loaded.Keys.Count);
        Assert.True(stopwatch.ElapsedMilliseconds < 50,
            $"Loading {material.Count} keys took {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void Loaded_keys_keep_their_identity()
    {
        var material = TestKeys.Keys.Keys
            .Select(k => (k.Certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, "pw"), (string?)"pw", k.Use))
            .ToList();

        using var loaded = KeyRing.Load(material);

        Assert.Equal(
            TestKeys.Keys.Keys.Select(k => k.Kid),
            loaded.Keys.Select(k => k.Kid));
    }

    [Fact]
    public void An_empty_key_ring_is_refused()
    {
        // A client fetching an empty JWKS fails with a key-resolution error that says nothing
        // about the cause, so this stops at the source instead.
        Assert.Throws<ArgumentException>(() => new KeyRing([]));
    }
}
