# syntax=docker/dockerfile:1.7
# ---- build stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build
WORKDIR /src

# Copy solution + project files first so dependency restore is cached.
COPY EventTriage.sln ./
COPY src/EventTriage.Api/EventTriage.Api.csproj   src/EventTriage.Api/
COPY tests/EventTriage.Tests/EventTriage.Tests.csproj tests/EventTriage.Tests/

RUN dotnet restore EventTriage.sln

# Now copy the rest of the sources and publish.
COPY . .
RUN dotnet publish src/EventTriage.Api/EventTriage.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

# ---- runtime stage --------------------------------------------------------
# chiseled (Ubuntu Chiseled) is a minimal, distroless-style image: no shell,
# no package manager, smaller attack surface.
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled AS runtime
WORKDIR /app

# Container Apps default port.
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

COPY --from=build /app/publish .

# Run as non-root (chiseled images already default to a non-root UID).
USER $APP_UID

ENTRYPOINT ["dotnet", "EventTriage.Api.dll"]
