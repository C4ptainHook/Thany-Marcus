namespace ThanyMarcus.Cloud.Api.Features.PluginAuth;

public sealed class RequirePluginAuthFilter(IPluginTokenAuthenticator auth) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) && http.Request.Query.TryGetValue("access_token", out var qsToken))
        {
            // EventSource cannot send Authorization headers; query-string fallback is the standard pattern
            var raw = qsToken.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                header = $"Bearer {raw}";
            }
        }
        var principal = await auth.AuthenticateAsync(header, http.RequestAborted);
        if (principal is null)
        {
            return Results.Unauthorized();
        }
        http.Items[HttpContextExtensions.PluginPrincipalKey] = principal;
        return await next(context);
    }
}
