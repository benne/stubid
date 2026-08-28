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

    /// <summary>401 with a WWW-Authenticate challenge and an empty body.</summary>
    Challenge,

    /// <summary>Anything else. Always a surprise worth looking at.</summary>
    Unclassified,
}

public static class DispositionClassifier
{
    public static Disposition Classify(RecordedExchange exchange)
    {
        var location = exchange.Header("Location");

        return exchange.StatusCode switch
        {
            404 => Disposition.NotFound,
            401 when exchange.Header("WWW-Authenticate") is not null
                     && exchange.ResponseBody.Length == 0 => Disposition.Challenge,
            >= 300 and < 400 when location?.Contains("/Error?errorId=", StringComparison.Ordinal) == true
                => Disposition.ErrorPage,
            >= 300 and < 400 when location?.Contains("/Account/Login", StringComparison.Ordinal) == true
                => Disposition.LoginRedirect,
            >= 400 when LooksLikeBareOAuthError(exchange.ResponseBody) => Disposition.BareJson,
            >= 200 and < 300 => Disposition.Success,
            _ => Disposition.Unclassified,
        };
    }

    private static bool LooksLikeBareOAuthError(byte[] body)
    {
        var text = System.Text.Encoding.UTF8.GetString(body).Trim();
        return text.StartsWith("{\"error\":", StringComparison.Ordinal)
            && !text.Contains("error_description", StringComparison.Ordinal);
    }
}
