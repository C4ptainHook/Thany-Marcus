FROM node:22-alpine AS spa-build
WORKDIR /spa
RUN corepack enable
COPY src/ThanyMarcus.Portal.Web/package.json src/ThanyMarcus.Portal.Web/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/ThanyMarcus.Portal.Web/ ./
RUN pnpm build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj", "src/ThanyMarcus.Portal.Api/"]
COPY ["src/ThanyMarcus.Shared/ThanyMarcus.Shared.csproj",         "src/ThanyMarcus.Shared/"]
RUN dotnet restore "src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj"
COPY src/ src/
COPY --from=spa-build /spa/build /src/src/ThanyMarcus.Portal.Api/wwwroot
RUN dotnet publish "src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj" -c Release -o /app /p:UseAppHost=false /p:SkipSpaBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ThanyMarcus.Portal.Api.dll"]
