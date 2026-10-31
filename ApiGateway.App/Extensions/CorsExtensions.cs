namespace ApiGateway.App.Extensions;

/// <summary>
/// Optional CORS for browser clients served from another origin (e.g. the local demo console at
/// <c>http://localhost:15100</c>). Off unless <see cref="AllowedOriginsKey"/> lists at least one origin.
/// </summary>
/// <remarks>
/// <para>
/// The policy lives at the gateway, not in each service: the gateway is the only public entry point,
/// so one policy covers every proxied route, and the preflight (<c>OPTIONS</c>) is answered here
/// without reaching a service that knows nothing about it.
/// </para>
/// <para>
/// Any method and header is allowed (the browser sends <c>Authorization</c> and <c>Content-Type</c>),
/// but no credentials: the platform authenticates with a bearer header, never with cookies, so
/// <c>AllowCredentials</c> would only widen the attack surface. <c>X-Correlation-Id</c> is exposed so
/// a browser client can read the id the gateway mints.
/// </para>
/// </remarks>
public static class CorsExtensions
{
    /// <summary>Configuration key: array of allowed origins (scheme + host + port, no trailing slash).</summary>
    public const string AllowedOriginsKey = "Cors:AllowedOrigins";

    /// <summary>Name of the single CORS policy the gateway registers.</summary>
    public const string PolicyName = "GatewayCors";

    /// <summary>Response headers a browser client may read.</summary>
    public static readonly string[] ExposedHeaders = ["X-Correlation-Id"];

    /// <summary>The configured origins, trimmed, without blanks or trailing slashes.</summary>
    public static string[] GetAllowedOrigins(IConfiguration configuration) =>
        (configuration.GetSection(AllowedOriginsKey).Get<string[]>() ?? [])
            .Select(origin => origin.Trim().TrimEnd('/'))
            .Where(origin => origin.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IServiceCollection AddGatewayCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = GetAllowedOrigins(configuration);
        if (origins.Length == 0)
        {
            return services;
        }

        services.AddCors(options => options.AddPolicy(PolicyName, policy => policy
            .WithOrigins(origins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders(ExposedHeaders)));

        return services;
    }

    /// <summary>
    /// Adds the CORS middleware when origins are configured. Call after <c>UseRouting</c> and before
    /// <c>MapReverseProxy</c> so preflights are answered by the gateway and every proxied response
    /// carries the headers.
    /// </summary>
    public static IApplicationBuilder UseGatewayCors(this WebApplication app)
    {
        if (GetAllowedOrigins(app.Configuration).Length > 0)
        {
            app.UseCors(PolicyName);
        }

        return app;
    }
}
