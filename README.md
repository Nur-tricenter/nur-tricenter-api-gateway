# API Gateway

YARP reverse proxy + Consul service discovery, modelled on
[`nur-university/ms2026-store-api-gateway`](https://github.com/nur-university/ms2026-store-api-gateway)
and adapted to our ecosystem.

The gateway is the **only** public entry point of the platform. Nothing talks to a microservice
directly; every call goes through `/api/{service-name}/...` and the gateway resolves the target
from Consul at runtime.

---

## How it works

```
GET /api/catalog/v1/foods
        │
        ▼
   ConsulYarpConfigProvider  ──every 30s──▶  Consul catalog + health
        │                                     (passingOnly = true)
        │  publishes an immutable IProxyConfig
        ▼
   YARP: PathRemovePrefix /api/catalog  +  PathPrefix /api
        │
        ▼
   http://10.0.0.1:8080/api/v1/foods
```

Three decisions carry the whole design:

1. **`Catalog.Services()` only lists names.** It says nothing about health.
   `Health.Service(name, tag: null, passingOnly: true)` is what actually filters out instances
   whose checks are failing, which is what makes a crashed-but-still-registered instance stop
   receiving traffic automatically.
2. **A service with zero healthy instances gets no route at all.** The route disappears from
   the table and YARP answers 404, instead of proxying into a black hole and burning the
   caller's timeout.
3. **Config is an immutable snapshot + change token.** YARP reads it from many request threads
   concurrently. After swapping in the new snapshot the *old* token is cancelled — that cancel
   is the signal YARP is waiting on. Cancelling only the new one (the natural mistake) leaves
   the gateway frozen on whatever it discovered at startup.

A Consul outage never fails the request path: the refresh is fully guarded, the error is logged,
and the last known-good route table keeps serving.

---

## Layout

```
ApiGateway.sln
ApiGateway.App/
├── Program.cs                             # minimal hosting + middleware order
├── DependencyInjection.cs                 # registration order, as a list of decisions
├── Config/ConsulYarpConfigProvider.cs     # Consul -> YARP bridge (the core of the gateway)
├── Extensions/ApplicationBuilderExtensions.cs  # Swagger UI, health endpoints, correlation id
├── appsettings.json
├── appsettings.Development.json
├── Properties/launchSettings.json
└── Dockerfile
ApiGateway.App.Tests/
└── ConsulYarpConfigProviderTests.cs       # xUnit + Moq
```

---

## Run it

```bash
dotnet build
dotnet test
dotnet run --project ApiGateway.App        # http://localhost:8080
```

Local endpoints:

| Path          | What                                                     |
|---------------|----------------------------------------------------------|
| `/swagger`    | Swagger UI (Development only) with a Consul-fed dropdown  |
| `/health`     | Health endpoint, Health Checks UI JSON format            |
| `/health-ui`  | Health Checks UI dashboard                               |

Docker:

```bash
docker build -t nur-tricenter-api-gateway -f ApiGateway.App/Dockerfile .
docker run -p 8080:8080 -e ServiceRegistration__ServiceDiscoveryAddress=http://host.docker.internal:8500 nur-tricenter-api-gateway
```

---

## Configuration

| Key | Where | Default | Meaning |
|---|---|---|---|
| `ServiceRegistration:ServiceDiscoveryAddress` | `appsettings.Development.json` | `http://localhost:8500` | Consul agent/base URL. **Required** — a missing or malformed value fails startup with an `InvalidOperationException` that names the key. |
| `HealthChecksUI:SelfHealthEndpoint` | `appsettings.Development.json` | `http://localhost:8080/health` | The gateway's own endpoint, shown on the `/health-ui` dashboard. |

### No `JwtOptions` — on purpose

Authentication is **deferred**, not forgotten. There is no `JwtOptions` section, no
`UseAuthentication`, no `UseAuthorization` and no access-rule filter in this project.

Two things are deliberately kept so the deferred work is a drop-in later:

- the **Bearer security definition in Swagger** (`DependencyInjection.AddGatewayDocumentation`),
  so consumers can already see the contract the platform will enforce;
- the absence of any authorization middleware, because registering one with no scheme behind it
  only produces misleading 401s that look like a bug.

---

## Divergences from the MS2026 reference

Everything below is a conscious deviation, not an accident.

### 1. Target framework: `net10.0`, not `net8.0`

The reference targets `net8.0`. Our whole ecosystem (`ms2-plans-and-recipes-catalog`) is on
`net10.0` and we are staying there deliberately. Nothing in this gateway depends on an
`net8`-only API, so the bump is mechanical.

### 2. `Swashbuckle.AspNetCore` 7.2.0, not 8.1.2

`Catalog.App` targets `net8.0`, so the gateway's `net10.0` is a deliberate divergence from the
reference. It consumes no shared kernel, so nothing here is constrained by it. Pinning one
Swashbuckle (and therefore
one `Microsoft.OpenApi` major version) across the platform is worth more than matching the
reference exactly. Practical consequence: in 7.x `UrlDescriptor` lives in
`Microsoft.OpenApi.Models`, whereas the reference (8.1.2 / OpenApi 2.0) needs a different
namespace.

### 3. No `Serilog` / `AddObservability` / `AddSecrets`

| Excluded | Why |
|---|---|
| `Nur.Store2026.Security` | JWT deferred by decision. |
| `Nur.Store2026.Access` | Only meaningful together with Security. |
| `Joseco.Secrets.HashicorpVault` | No secret material is consumed by the gateway yet; it only forwards requests. |
| `Nur.Store2026.Observability` | Logging/Seq/Jaeger is **our Fase 6**. The reference's `UseLogging(serviceName, ...)` call is dropped; the gateway uses the standard `Logging` configuration section until then. |
| `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` | We ship an explicit `Dockerfile`, so the extra tooling would only add a second source of truth for the container image. |

### 4. Guarded Consul address instead of `new Uri(value!)`

The reference writes:

```csharp
cfg.Address = new Uri(serviceDiscoveryAddress!);
```

With a missing key that surfaces as a bare `ArgumentNullException` thrown from inside Consul's
own code — useless when you are staring at `appsettings.json`. We validate the key and throw an
`InvalidOperationException` that names both the key and the offending value.

### 5. The Health Checks UI registration landmine was removed

The reference does this **during service registration**:

```csharp
services.AddHealthChecksUI(setup => {
    var registerdServices = services.BuildServiceProvider()
        .GetServices<IConsulClient>()
        .SelectMany(client => client.Agent.Services().Result.Response.Values)
        ...
});
```

Two defects, both fatal for a gateway:

1. `BuildServiceProvider()` inside `ConfigureServices` builds a **second** container — separate
   from the one the app runs on, never disposed, with duplicated singletons.
2. `.Result` blocks a thread on an **async HTTP call during DI**. An unreachable Consul turns
   "start the gateway" into a startup crash, which is exactly backwards: the whole point of the
   Consul integration is that discovery failures must not take the gateway down.

We register a fixed local endpoint list instead (`HealthChecksUI:SelfHealthEndpoint`).
Downstream services can be added explicitly through configuration.

### 6. `RefreshAsync()` is `internal` instead of fully private

`Refresh` is still the `async void` timer callback with the same 30 s period and `TimeSpan.Zero`
due time. The refresh body was extracted into an `internal Task RefreshAsync()` so the test
project can drive a refresh deterministically instead of racing the timer.

### 7. `KubernetesClient` pinned to 17.0.14

Not a divergence from the reference — a NuGet-level fix both projects need.
`AspNetCore.HealthChecks.UI` 9.0.0 depends on `KubernetesClient` 15.0.1, which carries
advisory **GHSA-w7r3-mgwf-4mqq / CVE-2025-9708** (improper certificate validation) and is only
patched from 17.0.14. We never touch a Kubernetes API — the package is referenced solely by the
UI's Kubernetes storage provider, which we do not register — so bumping it clears the advisory
with no behavioural risk. See the comment in `ApiGateway.App.csproj`.

### 8. `UseHttpsRedirection()` was not copied

The reference redirects to HTTPS. In front of a container that only speaks plain HTTP on
`:8080` this turns healthy requests into 307s. TLS termination belongs at the ingress/load
balancer, not duplicated in every application.

---

## Tests

```bash
dotnet test
```

`ConsulYarpConfigProviderTests` drives the provider with a mocked `IConsulClient` (which stays
injectable on purpose — that is what makes the gateway testable without a Consul agent). It
covers:

- routes/clusters are published for healthy services only, never for `consul` itself and never
  for a service with zero healthy instances;
- the exact transforms (`PathRemovePrefix = /api/catalog`, `PathPrefix = /api`) and the
  `{**catch-all}` match;
- `passingOnly: true` is actually used, and `consul` is never health-queried;
- a Consul failure logs and keeps the previous config instead of throwing;
- the previous snapshot's change token is signalled;
- `DangerousAcceptAnyServerCertificate` is set in Development and absent everywhere else;
- the constructor's immediate refresh really happens.

The provider refreshes on a timer with `TimeSpan.Zero` due time, so most tests call the internal
`RefreshAsync()` explicitly to stay deterministic; the one test that asserts the timer behaviour
uses bounded polling, not a fixed sleep.