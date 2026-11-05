# syntax=docker/dockerfile:1.7
# RedVital — Servicio Institucional. .NET 10 (Herramientas V4.0, entrada 7).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS construccion
WORKDIR /fuente
COPY src/ src/
RUN --mount=type=cache,id=redvital-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/RedVital.Institucional.Api/RedVital.Institucional.Api.csproj \
 && dotnet publish src/RedVital.Institucional.Api/RedVital.Institucional.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=construccion /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=QA \
    DOTNET_EnableDiagnostics=0
# Usuario sin privilegios que trae la imagen oficial.
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=6s --start-period=40s --retries=5 \
  CMD ["dotnet", "RedVital.Institucional.Api.dll", "--salud"]
ENTRYPOINT ["dotnet", "RedVital.Institucional.Api.dll"]
