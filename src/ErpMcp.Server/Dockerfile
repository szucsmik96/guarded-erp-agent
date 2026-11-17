# Build context: repo root.  docker build -f src/ErpMcp.Server/Dockerfile -t erp-mcp .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ErpMcp.Server/ErpMcp.Server.csproj src/ErpMcp.Server/
RUN dotnet restore src/ErpMcp.Server/ErpMcp.Server.csproj
COPY src/ErpMcp.Server/ src/ErpMcp.Server/
RUN dotnet publish src/ErpMcp.Server/ErpMcp.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The database lives on a volume; the non-root app user must own it.
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
ENV MCP_TRANSPORT=http \
    ASPNETCORE_HTTP_PORTS=8080 \
    ERP_DB_PATH=/data/erp.db
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --retries=5 CMD ["dotnet", "ErpMcp.Server.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "ErpMcp.Server.dll"]
