using ApiGateway.WebApp;
using ApiGateway.WebApp.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddServices(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
//
// A gateway is a reverse proxy, not an application. Two things that a web API
// project would normally have are deliberately absent:
//
//   - No authentication. There is no AddAuthentication, no UseAuthentication and
//     no UseAuthorization, so every proxied route is open. Phase 5b adds the
//     middleware, not just the registration: a scheme with nothing behind it
//     answers 401 to requests that are in fact allowed, which is worse than no
//     scheme at all.
//
//   - No Swagger UI, and no OpenAPI document. The guide gives the gateway one
//     job (route /api/{service}/** from Consul's health-filtered catalog) and
//     no documentation endpoint. A proxy has no controllers, so an OpenAPI
//     document generated from the app's own endpoints is empty, and one
//     synthesised from the route table would be a description of paths rather
//     than of operations: accurate about where traffic goes, useless about what
//     it expects to receive. Each service documents its own contract, which is
//     where a consumer should look for that.
app.UseRouting();

app.UseHealthChecks();
app.UseCorrelationId();

app.MapReverseProxy();

app.Run();