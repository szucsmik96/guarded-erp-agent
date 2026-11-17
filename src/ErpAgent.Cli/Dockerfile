# Build context: repo root.  docker build -f src/ErpAgent.Cli/Dockerfile -t erp-agent .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ErpAgent.Core/ErpAgent.Core.csproj src/ErpAgent.Core/
COPY src/ErpAgent.Cli/ErpAgent.Cli.csproj src/ErpAgent.Cli/
RUN dotnet restore src/ErpAgent.Cli/ErpAgent.Cli.csproj
COPY src/ErpAgent.Core/ src/ErpAgent.Core/
COPY src/ErpAgent.Cli/ src/ErpAgent.Cli/
RUN dotnet publish src/ErpAgent.Cli/ErpAgent.Cli.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir /audit && chown $APP_UID /audit
USER $APP_UID
ENV Agent__AuditFile=/audit/agent-audit.jsonl
ENTRYPOINT ["dotnet", "ErpAgent.Cli.dll"]
