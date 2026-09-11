namespace StubId.CaptureHarness;

/// <summary>
/// Where a run sends its requests and where it writes what comes back.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not called a profile. <c>IBrokerProfile</c> and <c>BrokerProfiles</c> already
/// exist on the server side and describe the emulator's personality — what it serves, and under
/// which path. This describes the recorder's address book, which is the opposite direction, and
/// naming them alike would guarantee somebody eventually tries to unify them. The harness has no
/// project references at all, and keeping it that way is what lets it record a broker the server
/// cannot yet serve.
/// </para>
/// <para>
/// <see cref="Key" /> is the same word the server uses for the profile, so one token is the
/// command-line argument, the fixture directory and the profile name.
/// </para>
/// </remarks>
public sealed record BrokerTarget
{
    /// <summary>Which broker, as the scrubber's credential table tags it.</summary>
    public required Broker Broker { get; init; }

    /// <summary>The <c>--broker=</c> value and the fixture directory: "neb", "signicat".</summary>
    public required string Key { get; init; }

    /// <summary>The broker as somebody would say it out loud.</summary>
    public required string Display { get; init; }

    /// <summary>The environment directory below the broker: "pp", "sandbox".</summary>
    public required string Environment { get; init; }

    /// <summary>The environment as prose, for a file somebody reads.</summary>
    /// <remarks>
    /// Separate from the directory name because one broker calls it pre-production and the other
    /// calls it a sandbox, and a generated document that said "pp" would be telling the reader
    /// about a directory rather than about the broker.
    /// </remarks>
    public required string EnvironmentDisplay { get; init; }

    /// <summary>
    /// The issuer, which may name a value the local settings hold.
    /// </summary>
    /// <remarks>
    /// Stored as a template rather than resolved, because Signicat's tenant is its hostname and
    /// the account's own subdomain is not something this repository knows. Resolving goes through
    /// the scrubber, which means an unset domain refuses a recording rather than writing somebody's
    /// account name into one.
    /// </remarks>
    public required string AuthorityTemplate { get; init; }

    /// <summary>The case whose recording is this broker's key set.</summary>
    /// <remarks>
    /// Named rather than assumed. Two things read it — the certificate report a capture writes,
    /// and the drift check in <see cref="Preflight" /> — and both used to spell "CAP-002" for
    /// themselves, outside the catalog that decides what CAP-002 is.
    /// </remarks>
    public required string KeySetCaptureId { get; init; }

    /// <summary>
    /// The algorithm this broker advertises for a request object.
    /// </summary>
    /// <remarks>
    /// What the broker says it takes, so <c>check</c> can compare it against the discovery
    /// document rather than a sitting discovering the answer. The first broker advertises HS256
    /// among others and accepts it, measured; the second advertises nine and not one of them is
    /// symmetric.
    /// </remarks>
    public required string RequestObjectAlgorithm { get; init; }

    /// <summary>
    /// The setting naming a file that holds the private key, where signing is asymmetric.
    /// </summary>
    /// <remarks>
    /// Null where the client secret signs. A path rather than the key itself: the guard that
    /// searches committed files reads every configured value as a substring to look for, and a
    /// multi-line PEM there would be meaningless.
    /// </remarks>
    public string? PrivateKeySetting { get; init; }

    /// <summary>The setting holding the identifier the broker gave the registered key.</summary>
    public string? KeyIdSetting { get; init; }

    /// <summary>
    /// What a redirect to this broker's own error page looks like.
    /// </summary>
    /// <remarks>
    /// Shared between the two so far, and declared per broker anyway. Both run IdentityServer, so
    /// both answer an unusable request with a redirect carrying a protected error id - which is a
    /// thing that turned out to be true rather than a thing to rely on.
    /// </remarks>
    public string ErrorMarker { get; init; } = "/Error?errorId=";

    /// <summary>
    /// What a redirect to this broker's own login page looks like, where that is known.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty means nobody has seen one. That is a deliberate state rather than a gap to fill in
    /// from the other broker: both are IdentityServer, so guessing its login path would be the
    /// dangerous kind of nearly-right, and Signicat's MitID journey is known to leave the tenant
    /// host entirely for a shim on the older platform. A guess would classify a refusal as an
    /// acceptance, or classify nothing and look like a broken probe.
    /// </para>
    /// <para>
    /// Nothing classifies as a login redirect while this is empty, and the first run prints the
    /// location it actually reached - which is the observation that fills it in.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> LoginMarkers { get; init; } = [];

    /// <summary>
    /// A substring of the subject of a certificate this broker must publish, or null.
    /// </summary>
    /// <remarks>
    /// Nets eID Broker signs transaction tokens with a key it publishes separately, and its
    /// absence is what a sitting recording one needs to know before it starts. A broker with no
    /// such key names nothing here, and the check is skipped rather than failed.
    /// </remarks>
    public string? CertificateSubjectMarker { get; init; }

    /// <summary>What signs a request object for one of this broker's clients.</summary>
    public RequestSigner SignerFor(BrokerClient client) => SignerFor(client, LocalSettings.Get);

    /// <inheritdoc cref="SignerFor(BrokerClient)"/>
    /// <remarks>With the resolver passed in, the way everything else here that reads settings does.</remarks>
    public RequestSigner SignerFor(BrokerClient client, Func<string, string?> resolve)
    {
        if (PrivateKeySetting is null)
        {
            return RequestSigner.ClientSecret(client.Secret(resolve));
        }

        var path = resolve(PrivateKeySetting) is { Length: > 0 } configured
            ? configured
            : throw new InvalidOperationException(
                $"Set {PrivateKeySetting} to the private key whose public half is registered on "
                + $"the {client.Name} client. {Display} signs request objects with a key, not "
                + "with a secret.");

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{PrivateKeySetting} names a file that is not there.");
        }

        return RequestSigner.PrivateKey(
            File.ReadAllText(path),
            KeyIdSetting is null ? null : resolve(KeyIdSetting));
    }

    /// <summary>The registrations a sitting against this broker records with.</summary>
    public IReadOnlyList<BrokerClient> Clients => [.. BrokerClient.All.Where(c => c.Broker == Broker)];

    /// <summary>The issuer with any configured value substituted in.</summary>
    /// <exception cref="InvalidOperationException">The settings do not hold it.</exception>
    public string Authority => Scrubber.Unscrub(AuthorityTemplate);

    /// <summary>
    /// The same, for a caller whose job is to report that it cannot be resolved.
    /// </summary>
    public bool TryResolveAuthority(out string authority) =>
        Scrubber.TryUnscrub(AuthorityTemplate, out authority);

    /// <summary>Where the key set is published.</summary>
    /// <remarks>
    /// The same unusual layout on both brokers, under the discovery document rather than beside
    /// it, which is one of the things that gave away that they are the same product underneath.
    /// </remarks>
    public string JwksUrl => $"{Authority}/.well-known/openid-configuration/jwks";

    /// <summary>Where the discovery document is published.</summary>
    public string DiscoveryUrl => $"{Authority}/.well-known/openid-configuration";

    /// <summary>The unattended pack, relative to the repository root.</summary>
    public string Pack => Path.Combine("fixtures", Key, Environment);

    /// <summary>The sitting's pack, which no run can reproduce.</summary>
    public string SessionPack => Path.Combine("fixtures", Key, Environment + "-session");

    /// <summary>Where the decoded certificate report for this broker's key set goes.</summary>
    public string CertificateReportPath => Path.Combine("fixtures", Key, "certificates.md");

    public static readonly BrokerTarget NetsEidBroker = new()
    {
        Broker = Broker.NetsEidBroker,
        Key = "neb",
        Display = "Nets eID Broker",
        Environment = "pp",
        EnvironmentDisplay = "pre-production",
        AuthorityTemplate = "https://pp.netseidbroker.dk/op",
        KeySetCaptureId = "CAP-002",
        CertificateSubjectMarker = "Transact",

        // Measured, and its recordings were made with it. Re-signing them differently would
        // change the segment lengths the sitting's fixtures record.
        RequestObjectAlgorithm = "HS256",

        LoginMarkers = ["/Account/Login"],
    };

    public static readonly BrokerTarget Signicat = new()
    {
        Broker = Broker.Signicat,
        Key = "signicat",
        Display = "Signicat",
        Environment = "sandbox",
        EnvironmentDisplay = "sandbox",

        // The tenant is the hostname here, which is the whole reason this is a template.
        AuthorityTemplate = "https://{{SIGNICAT_DOMAIN}}.sandbox.signicat.com/auth/open",
        KeySetCaptureId = "CAP-002",

        // Nine advertised and not one symmetric, so the client secret cannot sign here at all.
        RequestObjectAlgorithm = "RS256",
        PrivateKeySetting = "STUBID_SIGNICAT_PRIVATE_KEY_PATH",
        KeyIdSetting = "STUBID_SIGNICAT_KEY_ID",

        // Empty until a request reaches one and says where it landed. See LoginMarkers.
        LoginMarkers = [],

        // Nothing observed says this broker publishes a certificate for a separate signing key,
        // and a marker written from a guess would read as a check while checking nothing.
        CertificateSubjectMarker = null,
    };

    public static IReadOnlyList<BrokerTarget> All => [NetsEidBroker, Signicat];

    /// <summary>The target a <c>--broker=</c> value names.</summary>
    /// <remarks>
    /// There is no default, and the refusal says why rather than only that: the two write into
    /// different packs, and a run that guessed would put one broker's recordings where the
    /// other's belong.
    /// </remarks>
    public static BrokerTarget Select(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(Refusal("--broker= is required."));
        }

        return All.FirstOrDefault(t => string.Equals(t.Key, key.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                Refusal($"'{key.Trim()}' is not a broker this harness records."));
    }

    private static string Refusal(string opening) =>
        $"""
        {opening} It takes {string.Join(" or ", All.Select(t => $"'{t.Key}' ({t.Display})"))}.
        There is no default: the two write into different packs, and a run that guessed would
        put one broker's recordings where the other's belong.

          dotnet run --project tools/StubId.CaptureHarness -- capture --broker={All[0].Key}
        """;
}
