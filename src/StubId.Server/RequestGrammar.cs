using System.Text.Json;
using StubId.Abstractions;

namespace StubId.Server;

/// <summary>
/// The broker's own parameters, and which of them it refuses a request over.
/// </summary>
/// <remarks>
/// <para>
/// What is validated here and what is carried through is not a judgement call: it is recorded.
/// The broker rejects an <c>idp_values</c> it does not know (CAP-009) and an <c>idp_params</c>
/// that is not JSON (CAP-040), but accepts a malformed value <em>inside</em> a well-formed
/// <c>idp_params</c> (CAP-010) and a simulation mode it does not define (CAP-013). Copying
/// that shape matters: a stub that validated more would fail requests the broker accepts, and
/// a team would spend an afternoon fixing code that was never broken.
/// </para>
/// <para>
/// Scope is the one that would have been guessed wrong. Discovery publishes no
/// <c>scopes_supported</c>, so there is nothing to check a scope against, and the reasonable
/// assumption was that a missing one is carried through. CAP-043 says otherwise: the request
/// is refused outright.
/// </para>
/// </remarks>
public static class RequestGrammar
{
    /// <summary>
    /// The identity providers the broker names in its own error catalogue. An unknown value is
    /// refused, which CAP-009 establishes; these two are what CAP-007 and CAP-041 leave.
    /// </summary>
    private static readonly string[] KnownIdentityProviders = ["mitid", "mitid_erhverv"];

    /// <summary>Why a request cannot proceed, or null when it can.</summary>
    /// <remarks>
    /// The order is not observable: every recording carries one fault at a time, so no
    /// recording says which the broker complains about first. Client, then redirect, then the
    /// rest is the order a reader expects.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp/CAP-009, fixtures/neb/pp/CAP-040, fixtures/neb/pp/CAP-043")]
    public static (string Code, string Description)? Fault(
        AuthorizationRequest request, IReadOnlyDictionary<string, string> parameters)
    {
        if (string.IsNullOrEmpty(request.Scope))
        {
            return ("invalid_request", "Missing scope.");
        }

        foreach (var idp in IdentityProviders(parameters))
        {
            if (!KnownIdentityProviders.Contains(idp, StringComparer.Ordinal))
            {
                return ("invalid_request", $"Unknown idp_values entry '{idp}'.");
            }
        }

        if (parameters.TryGetValue("idp_params", out var raw) && raw.Length > 0 && !IsJsonObject(raw))
        {
            // The broker's own code for it: "Typically, invalid encoding of this parameter."
            return ("invalid_idp_params", "idp_params is not URL-encoded JSON.");
        }

        return null;
    }

    /// <summary>A space-delimited list, which is how the broker spells more than one.</summary>
    public static IReadOnlyList<string> IdentityProviders(IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue("idp_values", out var value)
            ? value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>
    /// The per-provider object, decoded but not inspected. CAP-010 records a nonsense
    /// <c>uuid_hint</c> being accepted here and failing later, inside the MitID flow, which is
    /// why the broker publishes an error code for it at all.
    /// </summary>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp/CAP-010/response.head")]
    public static IReadOnlyDictionary<string, string> IdentityProviderParameters(
        IReadOnlyDictionary<string, string> parameters, string idp)
    {
        if (!parameters.TryGetValue("idp_params", out var raw) || raw.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(raw);

            return document.RootElement.TryGetProperty(idp, out var section)
                   && section.ValueKind == JsonValueKind.Object
                ? section.EnumerateObject().ToDictionary(
                    m => m.Name,
                    m => m.Value.ValueKind == JsonValueKind.String ? m.Value.GetString()! : m.Value.ToString(),
                    StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static bool IsJsonObject(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
