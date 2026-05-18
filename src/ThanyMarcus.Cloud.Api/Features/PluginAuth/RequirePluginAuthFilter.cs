namespace ThanyMarcus.Cloud.Api.Features.PluginAuth;

public sealed class RequirePluginAuthFilter(IPluginTokenAuthenticator auth) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        var principal = await auth.AuthenticateAsync(header, http.RequestAborted);
        if (principal is null)
        {
            return Results.Unauthorized();
        }
        http.Items[HttpContextExtensions.PluginPrincipalKey] = principal;
        return await next(context);
    }
}
