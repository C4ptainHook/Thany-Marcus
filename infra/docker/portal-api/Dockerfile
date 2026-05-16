FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj", "src/ThanyMarcus.Portal.Api/"]
COPY ["src/ThanyMarcus.Shared/ThanyMarcus.Shared.csproj",         "src/ThanyMarcus.Shared/"]
RUN dotnet restore "src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj"
COPY src/ src/
RUN dotnet publish "src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj" -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ThanyMarcus.Portal.Api.dll"]
