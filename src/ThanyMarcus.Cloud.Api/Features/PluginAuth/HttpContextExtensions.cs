namespace ThanyMarcus.Cloud.Api.Features.PluginAuth;

public static class HttpContextExtensions
{
    public const string PluginPrincipalKey = "__plugin_principal";

    public static PluginPrincipal GetPluginPrincipal(this HttpContext ctx) =>
        ctx.Items[PluginPrincipalKey] as PluginPrincipal
            ?? throw new InvalidOperationException(
                "Plugin principal not on HttpContext; endpoint must be wrapped with RequirePluginAuthFilter.");
}
