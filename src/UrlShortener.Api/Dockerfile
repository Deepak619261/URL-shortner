# Multi-stage build: smaller final image, no SDK in production

# Stage 1: build with full SDK
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY UrlShortener.Api.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app

# Stage 2: runtime image (no SDK, much smaller)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Render and most PaaS platforms set PORT env var; bind Kestrel to it
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "UrlShortener.Api.dll"]
