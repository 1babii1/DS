namespace McpServer.Api;

public enum ServiceApiFailure
{
    Unauthorized,
    Forbidden,
    NotFound,
    RateLimited,
    Unavailable,
}

// What a tool is allowed to tell the model about a failed downstream call: a category and a fixed
// sentence, never the downstream response body, which can carry details the caller was not meant
// to see.
public sealed class ServiceApiException(ServiceApiFailure failure, string message) : Exception(message)
{
    public ServiceApiFailure Failure { get; } = failure;

    public static ServiceApiException From(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized => new(ServiceApiFailure.Unauthorized, "Your session is not valid for this service."),
        System.Net.HttpStatusCode.Forbidden => new(ServiceApiFailure.Forbidden, "You are not allowed to do this."),
        System.Net.HttpStatusCode.NotFound => new(ServiceApiFailure.NotFound, "Not found."),
        System.Net.HttpStatusCode.TooManyRequests => new(ServiceApiFailure.RateLimited, "Too many requests, try again shortly."),
        _ => new(ServiceApiFailure.Unavailable, "The service could not complete the request."),
    };
}
