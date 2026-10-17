using ApiGateway.WebApp;
using ApiGateway.WebApp.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddServices(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
//
// Note what is NOT here: there is no UseAuthentication / UseAuthorization. JWT validation
// was explicitly deferred, and registering an authorization middleware with no scheme
// behind it only produces misleading 401s. Swagger still documents the Bearer contract
// (see DependencyInjection.AddGatewayDocumentation) so the future shape is explicit.
app.UseRouting();

if (app.Environment.IsDevelopment())
{
    // Swagger UI dropdown is fed from Consul, so it is only meaningful in Development.
    // It is mounted before UseCorrelationId on purpose (this is the registration order we
    // settled on), which means the Swagger UI's own static assets - /swagger/index.js and
    // friends - are answered before the correlation-id middleware and therefore carry no
    // X-Correlation-Id. Everything the gateway actually proxies does.
    app.UseSwaggerWithUiWithConsul();
}

app.UseHealthChecks();
app.UseCorrelationId();

app.MapControllers();
app.MapReverseProxy();

app.Run();