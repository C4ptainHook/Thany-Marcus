using System.Security.Cryptography;
using System.Text;
using ThanyMarcus.Cloud.Api.Features.Bootstrap;

namespace ThanyMarcus.Cloud.Api.Features.Settings;

public sealed class RequireCloudAdminTokenFilter(BootstrapOptions opts) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        const string Prefix = "Bearer ";
        if (!header.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }

        var presented = header[Prefix.Length..].Trim();
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(opts.CloudAdminToken);

        if (presentedBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }
}
