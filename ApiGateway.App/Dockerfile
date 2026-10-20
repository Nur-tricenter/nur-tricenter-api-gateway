# syntax=docker/dockerfile:1

# ---- build stage -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, as a separate layer: source changes then do not invalidate the
# (expensive) NuGet restore cache.
COPY ApiGateway.App/ApiGateway.App.csproj ApiGateway.App/
RUN dotnet restore ApiGateway.App/ApiGateway.App.csproj

COPY ApiGateway.App/ ApiGateway.App/
RUN dotnet publish ApiGateway.App/ApiGateway.App.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ---- runtime stage -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1

EXPOSE 8080

COPY --from=build /app/publish .

# Never run the gateway as root: it is an internet-facing process.
USER $APP_UID

ENTRYPOINT ["dotnet", "ApiGateway.App.dll"]