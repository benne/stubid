namespace StubId.CaptureHarness;

/// <summary>
/// How the broker answers a request. This is the thing worth pinning: a status code alone
/// does not distinguish "rejected the request" from "accepted it and sent the user on",
/// because both are a 302.
/// </summary>
public enum Disposition
{
    /// <summary>2xx with a body.</summary>
    Success,

    /// <summary>404. The path is not served at all.</summary>
    NotFound,

    /// <summary>
    /// 302 to the broker's own login page. This is what an accepted authorize request looks
    /// like.
    /// </summary>
    LoginRedirect,

    /// <summary>
    /// 302 to the broker's error page carrying an opaque error id. An invalid authorize
    /// request lands here and is never redirected back to the client, so the client sees
    /// nothing at all.
    /// </summary>
    ErrorPage,

    /// <summary>
    /// 4xx whose body is a bare OAuth error object, with no description and no error_uri.
    /// </summary>
    BareJson,

    /// <summary>
    /// 4xx whose body is an OAuth error object that says more than the code.
    /// </summary>
    /// <remarks>
    /// The first broker sends the bare form, which is a finding about that broker rather than a
    /// fact about OAuth, and naming the member for it left nothing to classify the other shape
    /// as. A client reading one and then the other gets a different amount of help, and which it
    /// gets is exactly the sort of thing an emulator smooths over without meaning to.
    /// </remarks>
    DescribedJson,

    /// <summary>401 with a WWW-Authenticate challenge and an empty body.</summary>
    Challenge,

    /// <summary>
    /// 302 back to the client's own redirect_uri. Either a success carrying a code, or a
    /// user-level failure carrying error and error_description - the broker tells the client
    /// about those, unlike an invalid request.
    /// </summary>
    ClientRedirect,

    /// <summary>
    /// 200 whose body is a self-submitting form. What response_mode=form_post produces, which
    /// is ASP.NET Core's default, so it is the shape most .NET integrations actually receive.
    /// </summary>
    FormPost,

    /// <summary>Anything else. Always a surprise worth looking at.</summary>
    Unclassified,
}

public static class DispositionClassifier
{
    public static Disposition Classify(
        RecordedExchange exchange, BrokerTarget target, string? clientRedirectUri = null)
    {
        var location = exchange.Header("Location");

        if (clientRedirectUri is not null
            && location?.StartsWith(clientRedirectUri, StringComparison.OrdinalIgnoreCase) == true)
        {
            return Disposition.ClientRedirect;
        }

        if (exchange.StatusCode is >= 200 and < 300 && LooksLikeFormPost(exchange.ResponseBody))
        {
            return Disposition.FormPost;
        }

        return exchange.StatusCode switch
        {
            404 => Disposition.NotFound,
            401 when exchange.Header("WWW-Authenticate") is not null
                     && exchange.ResponseBody.Length == 0 => Disposition.Challenge,
            >= 300 and < 400 when location?.Contains(target.ErrorMarker, StringComparison.Ordinal) == true
                => Disposition.ErrorPage,

            // Nothing matches while a broker declares no login marker, which is the point: a
            // 3xx to somewhere unrecognized stays Unclassified and the run says where it went.
            >= 300 and < 400 when target.LoginMarkers.Any(
                marker => location?.Contains(marker, StringComparison.Ordinal) == true)
                => Disposition.LoginRedirect,

            >= 400 when LooksLikeOAuthError(exchange.ResponseBody, out var described) =>
                described ? Disposition.DescribedJson : Disposition.BareJson,
            >= 200 and < 300 => Disposition.Success,
            _ => Disposition.Unclassified,
        };
    }

    private static bool LooksLikeFormPost(byte[] body)
    {
        var text = System.Text.Encoding.UTF8.GetString(body);
        return text.Contains("<form", StringComparison.OrdinalIgnoreCase)
            && text.Contains("method=\"post\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An OAuth error object, and whether it says anything beyond the code.
    /// </summary>
    /// <remarks>
    /// One predicate for both shapes. Asking only whether a body was the bare form meant the
    /// other one - which the second broker is expected to send - fell through to Unclassified,
    /// where it would read as a surprise rather than as the answer.
    /// </remarks>
    private static bool LooksLikeOAuthError(byte[] body, out bool described)
    {
        var text = System.Text.Encoding.UTF8.GetString(body).Trim();
        described = text.Contains("error_description", StringComparison.Ordinal)
            || text.Contains("error_uri", StringComparison.Ordinal);

        return text.StartsWith("{\"error\":", StringComparison.Ordinal);
    }
}
