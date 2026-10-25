# Build from the repository root: docker build -f src/Contrapost.Worker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY db/ db/
COPY src/ src/
RUN dotnet publish src/Contrapost.Worker/Contrapost.Worker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Contrapost.Worker.dll"]
