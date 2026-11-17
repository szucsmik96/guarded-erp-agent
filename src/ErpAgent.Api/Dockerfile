# Build context: repo root.  docker build -f src/ErpAgent.Api/Dockerfile -t erp-agent-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ErpAgent.Core/ErpAgent.Core.csproj src/ErpAgent.Core/
COPY src/ErpAgent.Api/ErpAgent.Api.csproj src/ErpAgent.Api/
RUN dotnet restore src/ErpAgent.Api/ErpAgent.Api.csproj
COPY src/ErpAgent.Core/ src/ErpAgent.Core/
COPY src/ErpAgent.Api/ src/ErpAgent.Api/
RUN dotnet publish src/ErpAgent.Api/ErpAgent.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ErpAgent.Api.dll"]
