using System.Net;

namespace Activout.MemberData;

/// <summary>Error response from the LinkedIn API. The message never contains the access token.</summary>
public class MemberDataApiException(HttpStatusCode statusCode, string message)
    : Exception($"{(int)statusCode} {message}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>The API's own message, without the status prefix.</summary>
    public string ApiMessage { get; } = message;

    /// <summary>401: the token is missing, invalid, expired or revoked. A new token is required.</summary>
    public bool IsAuthentication => StatusCode == HttpStatusCode.Unauthorized;

    /// <summary>403: the token lacks the scope or the application lacks the product.</summary>
    public bool IsPermission => StatusCode == HttpStatusCode.Forbidden;

    /// <summary>404 "No data found …": the snapshot domain (or the remaining pages) has no data.</summary>
    public bool IsNoData => StatusCode == HttpStatusCode.NotFound &&
                            ApiMessage.StartsWith("No data found", StringComparison.OrdinalIgnoreCase);

    /// <summary>400 "The following domain is not supported: …".</summary>
    public bool IsUnsupportedDomain => StatusCode == HttpStatusCode.BadRequest &&
                                       ApiMessage.Contains("domain is not supported", StringComparison.OrdinalIgnoreCase);

    /// <summary>Worth retrying later: rate limiting, timeouts and server errors.</summary>
    public bool IsTransient => StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout ||
                               (int)StatusCode >= 500;
}
