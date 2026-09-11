namespace StubId.CaptureHarness;

/// <summary>
/// A client registration a step records with, and where its credentials come from.
/// </summary>
/// <remarks>
/// <para>
/// This replaced an enum whose seven values all resolved to one broker's setting names. A second
/// broker's clients are four different registrations configured differently, and one enum holding
/// both brokers' values would have been a list nobody could read as either.
/// </para>
/// <para>
/// <see cref="Summary" /> is not decoration. On the second broker a claim is a property of the
/// client as much as of the broker — its dashboard aliases claims per client, and one setting
/// decides whether a claim appears in the identity token at all — so a recording that does not
/// say which configuration produced it is not evidence of anything.
/// </para>
/// </remarks>
public sealed record BrokerClient
{
    /// <summary>The broker this registration belongs to.</summary>
    public required Broker Broker { get; init; }

    /// <summary>What the sitting's documentation calls it.</summary>
    public required string Name { get; init; }

    /// <summary>The configuration this client is in, as a reader of a recording needs it.</summary>
    public required string Summary { get; init; }

    /// <summary>The identifier, where the broker publishes it for anyone to use.</summary>
    public string? PublishedId { get; init; }

    /// <summary>The setting holding the identifier, where it is not published.</summary>
    public string? IdSetting { get; init; }

    /// <summary>The setting holding the secret.</summary>
    public required string SecretSetting { get; init; }

    public string ClientId() => ClientId(LocalSettings.Get);

    public string Secret() => Secret(LocalSettings.Get);

    /// <summary>With the settings resolver passed in.</summary>
    /// <remarks>
    /// The same reason the scrubber takes one: a test that reads the machine's configuration
    /// passes on a fresh checkout and fails the day somebody sets their machine up to record.
    /// Clearing an environment variable is not a substitute, because the file answers
    /// underneath it.
    /// </remarks>
    public string ClientId(Func<string, string?> resolve) => PublishedId ?? Require(IdSetting, resolve);

    /// <inheritdoc cref="ClientId(Func{string, string?})"/>
    public string Secret(Func<string, string?> resolve) => Require(SecretSetting, resolve);

    private string Require(string? setting, Func<string, string?> resolve) => setting is null
        ? throw new InvalidOperationException($"The {Name} client names no setting for this.")
        : resolve(setting) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Set {setting} to record with the {Name} client.");

    /// <summary>Every registration the harness knows, across every broker.</summary>
    public static IReadOnlyList<BrokerClient> All =>
        [.. NetsEidBroker.All, .. Signicat.All];

    /// <summary>Signaturgruppen's, as the first sitting used them.</summary>
    public static class NetsEidBroker
    {
        public static readonly BrokerClient Private = new()
        {
            Broker = CaptureHarness.Broker.NetsEidBroker,
            Name = "private",
            Summary = "a customer's own client, carrying the richer scopes",
            IdSetting = "STUBID_NEB_PP_CLIENT_ID",
            SecretSetting = "STUBID_NEB_PP_CLIENT_SECRET",
        };

        public static readonly BrokerClient OpenCode = new()
        {
            Broker = CaptureHarness.Broker.NetsEidBroker,
            Name = "open code",
            Summary = "the broker's published code-flow client",
            PublishedId = "0a775a87-878c-4b83-abe3-ee29c720c3e7",
            SecretSetting = "STUBID_NEB_PP_CODE_CLIENT_SECRET",
        };

        public static readonly BrokerClient OpenImplicit = new()
        {
            Broker = CaptureHarness.Broker.NetsEidBroker,
            Name = "open implicit",
            Summary = "the broker's published implicit client, for a front-channel id_token",
            PublishedId = "93ed8e0d-93ad-405c-b1ac-8bf13d484941",
            SecretSetting = "STUBID_NEB_PP_CODE_CLIENT_SECRET",
        };

        /// <summary>
        /// The same registration as <see cref="SsoA" />, used for what its redirect list refuses.
        /// </summary>
        /// <remarks>
        /// The published clients accept any redirect URI, so neither can record how the broker
        /// answers one it does not know. This one has "accept all redirects" off with a single
        /// URI registered, which is what makes the refusal recordable at all.
        /// </remarks>
        public static readonly BrokerClient Restricted = new()
        {
            Broker = CaptureHarness.Broker.NetsEidBroker,
            Name = "restricted redirect",
            Summary = "single sign-on client A, with only one redirect URI registered",
            IdSetting = "STUBID_NEB_PP_SSO_A_CLIENT_ID",
            SecretSetting = "STUBID_NEB_PP_SSO_A_CLIENT_SECRET",
        };

        public static readonly BrokerClient SsoA = new()
        {
            Broker = CaptureHarness.Broker.NetsEidBroker,
            Name = "single sign-on A",
            Summary = "first of a pair joined to one service provider's single sign-on",
            IdSetting = "STUBID_NEB_PP_SSO_A_CLIENT_ID",
            SecretSetting = "STUBID_NEB_PP_SSO_A_CLIENT_SECRET",
        };

        public static readonly BrokerClient SsoB = new()
        {
            Broker = CaptureHarness.Broker.NetsEidBroker,
            Name = "single sign-on B",
            Summary = "the second of that pair; reaching it without a prompt is the recording",
            IdSetting = "STUBID_NEB_PP_SSO_B_CLIENT_ID",
            SecretSetting = "STUBID_NEB_PP_SSO_B_CLIENT_SECRET",
        };

        public static readonly BrokerClient Hybrid = new()
        {
            Broker = CaptureHarness.Broker.NetsEidBroker,
            Name = "hybrid",
            Summary = "the hybrid grant, which is the only way to observe c_hash",
            IdSetting = "STUBID_NEB_PP_SSO_C_CLIENT_ID",
            SecretSetting = "STUBID_NEB_PP_SSO_C_CLIENT_SECRET",
        };

        public static IReadOnlyList<BrokerClient> All =>
            [Private, OpenCode, OpenImplicit, Restricted, SsoA, SsoB, Hybrid];
    }

    /// <summary>
    /// Signicat's, which do not exist yet.
    /// </summary>
    /// <remarks>
    /// Four registrations are planned and differ only in a handful of toggles, but a client this
    /// harness has never authenticated against is a guess with a setting name attached. They
    /// arrive with the catalog that uses them.
    /// </remarks>
    public static class Signicat
    {
        public static IReadOnlyList<BrokerClient> All => [];
    }
}
