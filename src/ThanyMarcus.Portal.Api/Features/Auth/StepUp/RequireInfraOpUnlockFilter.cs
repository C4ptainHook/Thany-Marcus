using System.Security.Claims;
using System.Security.Cryptography;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public sealed class RequireInfraOpUnlockFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var sub  = http.User.FindFirstValue(AuthClaimTypes.SubUs);
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return Results.Unauthorized();

        var cache = http.RequestServices.GetRequiredService<IInfraOpUnlockCache>();
        var probe = new byte[32];
        try
        {
            if (!await cache.TryGetAsync(userId, probe, http.RequestAborted))
                return Results.Json(new { error = "step_up_required" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(probe);
        }
        return await next(context);
    }
}
