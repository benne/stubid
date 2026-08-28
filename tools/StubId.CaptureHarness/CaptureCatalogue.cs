namespace StubId.CaptureHarness;

/// <summary>
/// The recordings that need no MitID login, and so can run unattended.
/// </summary>
/// <remarks>
/// CAP-001 to CAP-019 are this pack. CAP-020 onwards need a human to complete a login in
/// MitID's test tool, and live in a separate catalogue.
/// </remarks>
public static class CaptureCatalogue
{
    public const string PreProduction = "https://pp.netseidbroker.dk/op";
    public const string Production = "https://netseidbroker.dk/op";

    /// <summary>The broker publishes this client for anyone to use against pre-production.</summary>
    public const string OpenCodeClient = "0a775a87-878c-4b83-abe3-ee29c720c3e7";

    private const string RedirectUri = "http://localhost:5099/callback";

    /// <summary>Masks the data-protection blob in an error redirect, which is per-request.</summary>
    private const string ErrorIdIsVolatile = @"errorId=[A-Za-z0-9_\-]+";

    private static string Authorize(string extra) =>
        $"{PreProduction}/connect/authorize?client_id={OpenCodeClient}" +
        $"&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        $"&scope={Uri.EscapeDataString("openid mitid")}&state=capture&nonce=capture{extra}";

    public static IReadOnlyList<CaptureCase> All =>
    [
        new()
        {
            Id = "CAP-001",
            Expected = Disposition.Success,
            Description = "Pre-production discovery document",
            Settles = "The member set, member order, and the three keys the broker omits: "
                + "scopes_supported, claims_supported and acr_values_supported. Also the two "
                + "auth-method arrays advertised for endpoints that are never published.",
            Url = $"{PreProduction}/.well-known/openid-configuration",
        },
        new()
        {
            Id = "CAP-002",
            Expected = Disposition.Success,
            Description = "Pre-production signing keys",
            Settles = "JWKS shape: no alg member, uppercase 40-hex kid, x5t, single-element "
                + "x5c, member order. Decoding the certificates also identifies which key "
                + "signs which token type.",
            Url = $"{PreProduction}/.well-known/openid-configuration/jwks",
        },
        new()
        {
            Id = "CAP-003",
            Expected = Disposition.NotFound,
            Description = "RFC 8414 discovery layout is not served",
            Settles = "That StubID must answer 404 here. Spring probes the OpenID layout "
                + "first and succeeds, so serving this would let a broken client pass.",
            Url = "https://pp.netseidbroker.dk/.well-known/openid-configuration/op",
        },
        new()
        {
            Id = "CAP-004",
            Expected = Disposition.NotFound,
            Description = "OAuth authorisation-server metadata layout is not served",
            Settles = "The third layout Spring probes. Also 404.",
            Url = "https://pp.netseidbroker.dk/.well-known/oauth-authorization-server/op",
        },
        new()
        {
            Id = "CAP-005",
            Expected = Disposition.NotFound,
            Description = "Discovery is not served at the host root",
            Settles = "That the issuer's path segment is load-bearing: metadata exists only "
                + "under /op.",
            Url = "https://pp.netseidbroker.dk/.well-known/openid-configuration",
        },
        new()
        {
            Id = "CAP-006",
            Expected = Disposition.Success,
            Description = "Production discovery document",
            Settles = "That production and pre-production differ only by host, which is what "
                + "lets one profile serve both.",
            Url = $"{Production}/.well-known/openid-configuration",
        },
        new()
        {
            Id = "CAP-007",
            Expected = Disposition.Success,
            Description = "Error code catalogue",
            Settles = "The PascalCase envelope, the code count, and that per-code members are "
                + "sparse rather than uniform.",
            Url = $"{PreProduction}/api/v1/documentation/errorcodes",
        },
        new()
        {
            Id = "CAP-008",
            Expected = Disposition.ErrorPage,
            Description = "Authorize with an unknown client_id",
            Settles = "That an invalid request never redirects back to the client. It goes to "
                + "the broker's own error page instead.",
            Url = $"{PreProduction}/connect/authorize?client_id=00000000-0000-0000-0000-000000000000"
                + $"&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                + $"&scope={Uri.EscapeDataString("openid mitid")}&state=capture&nonce=capture",
            VolatileBodyPatterns = [ErrorIdIsVolatile],
            VolatileHeaders = ["Location"],
        },
        new()
        {
            Id = "CAP-009",
            Expected = Disposition.ErrorPage,
            Description = "Authorize naming an identity provider that does not exist",
            Settles = "That idp_values is validated at the authorize endpoint, and rejected "
                + "the same way as an unknown client.",
            Url = Authorize("&idp_values=nosuchidp"),
            VolatileBodyPatterns = [ErrorIdIsVolatile],
            VolatileHeaders = ["Location"],
        },
        new()
        {
            Id = "CAP-010",
            Expected = Disposition.LoginRedirect,
            Description = "Authorize with a malformed uuid_hint",
            Settles = "That values inside idp_params are NOT validated at the authorize "
                + "endpoint, even though idp_values is (CAP-009). A malformed uuid_hint is "
                + "accepted here and only fails later in the MitID flow, which is why the "
                + "broker publishes a mitid_uuid_hint_malformed error code at all. StubID "
                + "must not reject it up front.",
            Url = Authorize("&idp_values=mitid&idp_params="
                + Uri.EscapeDataString("""{"mitid":{"uuid_hint":"not-a-uuid"}}""")),
            VolatileBodyPatterns = [ErrorIdIsVolatile],
            VolatileHeaders = ["Location"],
        },
        new()
        {
            Id = "CAP-011",
            Expected = Disposition.ErrorPage,
            Description = "Authorize without response_type",
            Settles = "Whether a missing required parameter is refused the same way as an "
                + "invalid one.",
            Url = $"{PreProduction}/connect/authorize?client_id={OpenCodeClient}"
                + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                + $"&scope={Uri.EscapeDataString("openid mitid")}&state=capture&nonce=capture",
            VolatileBodyPatterns = [ErrorIdIsVolatile],
            VolatileHeaders = ["Location"],
        },
        new()
        {
            Id = "CAP-012",
            Expected = Disposition.LoginRedirect,
            Description = "Authorize with a valid request",
            Settles = "The accepted path: a 302 to the broker's login page whose ReturnUrl "
                + "carries the original query re-encoded under /op/connect/authorize/callback. "
                + "Also confirms an arbitrary localhost redirect_uri is accepted.",
            Url = Authorize("&idp_values=mitid"),
        },
        new()
        {
            Id = "CAP-013",
            Expected = Disposition.LoginRedirect,
            Description = "Authorize with a malformed simulation mode",
            Settles = "That the simulation parameter is not processed for a client without "
                + "the entitlement. A client that had it would reject this value, so reaching "
                + "the login page is the evidence that recordings cannot be automated.",
            Url = Authorize("&idp_values=mitid&simulation=totally-invalid-mode"),
        },
        new()
        {
            Id = "CAP-014",
            Expected = Disposition.BareJson,
            Description = "Token endpoint with a bad client secret",
            Settles = "That token errors are bare JSON with no error_description and no "
                + "error_uri.",
            Method = "POST",
            Url = $"{PreProduction}/connect/token",
            Form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = "not-a-real-code",
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = OpenCodeClient,
                ["client_secret"] = "wrong-secret",
            },
        },
        new()
        {
            Id = "CAP-015",
            Expected = Disposition.BareJson,
            Description = "Token endpoint with a valid client and an unusable code",
            Settles = "invalid_grant, and that a correctly authenticated client gets no more "
                + "detail than a badly authenticated one.",
            Method = "POST",
            Url = $"{PreProduction}/connect/token",
            Form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = "not-a-real-code",
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = OpenCodeClient,
                ["client_secret"] = "{{NEB_PP_OPEN_CLIENT_CODE_SECRET}}",
            },
        },
        new()
        {
            Id = "CAP-016",
            Expected = Disposition.BareJson,
            Description = "Client credentials for a scope the client may not have",
            Settles = "unauthorized_client, the third distinct token error shape.",
            Method = "POST",
            Url = $"{PreProduction}/connect/token",
            Form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "signtext_api",
                ["client_id"] = OpenCodeClient,
                ["client_secret"] = "{{NEB_PP_OPEN_CLIENT_CODE_SECRET}}",
            },
        },
        new()
        {
            Id = "CAP-017",
            Expected = Disposition.Challenge,
            Description = "Userinfo without a token",
            Settles = "The WWW-Authenticate byte string, including the absence of a space "
                + "after the comma, and that the body is empty rather than a JSON error.",
            Url = $"{PreProduction}/connect/userinfo",
        },
        new()
        {
            Id = "CAP-018",
            Expected = Disposition.Challenge,
            Description = "CPR match without a token",
            Settles = "That this endpoint challenges differently from userinfo. Two endpoints "
                + "on one host with two different WWW-Authenticate strings is the kind of "
                + "detail a generated emulator would smooth over.",
            Method = "POST",
            Url = $"{PreProduction}/api/v1/mitid/matchCpr",
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
        },
        new()
        {
            Id = "CAP-019",
            Expected = Disposition.BareJson,
            Description = "Pushed authorisation request without client authentication",
            Settles = "How PAR refuses an unauthenticated push. Discovery advertises the "
                + "endpoint, so .NET clients use it by default and reach this path first.",
            Method = "POST",
            Url = $"{PreProduction}/connect/par",
            Form = new Dictionary<string, string>
            {
                ["client_id"] = OpenCodeClient,
                ["response_type"] = "code",
                ["redirect_uri"] = RedirectUri,
                ["scope"] = "openid mitid",
            },
        },
    ];
}
