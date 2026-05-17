FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["src/ThanyMarcus.Cloud.Api/ThanyMarcus.Cloud.Api.csproj", "src/ThanyMarcus.Cloud.Api/"]
COPY ["src/ThanyMarcus.Shared/ThanyMarcus.Shared.csproj",       "src/ThanyMarcus.Shared/"]
RUN dotnet restore "src/ThanyMarcus.Cloud.Api/ThanyMarcus.Cloud.Api.csproj"
COPY src/ src/
RUN dotnet publish "src/ThanyMarcus.Cloud.Api/ThanyMarcus.Cloud.Api.csproj" \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ThanyMarcus.Cloud.Api.dll"]
