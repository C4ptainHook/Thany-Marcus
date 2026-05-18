FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["src/ThanyMarcus.Portal.SagaWorker/ThanyMarcus.Portal.SagaWorker.csproj", "src/ThanyMarcus.Portal.SagaWorker/"]
COPY ["src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj",               "src/ThanyMarcus.Portal.Api/"]
COPY ["src/ThanyMarcus.Shared/ThanyMarcus.Shared.csproj",                       "src/ThanyMarcus.Shared/"]
RUN dotnet restore "src/ThanyMarcus.Portal.SagaWorker/ThanyMarcus.Portal.SagaWorker.csproj"
COPY src/ src/
RUN dotnet publish "src/ThanyMarcus.Portal.SagaWorker/ThanyMarcus.Portal.SagaWorker.csproj" -c Release -o /app /p:UseAppHost=false /p:SkipSpaBuild=true

FROM hashicorp/terraform:1.7 AS terraform

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends bash ca-certificates curl libgssapi-krb5-2 \
    && curl -fsSL https://letsencrypt.org/certs/staging/letsencrypt-stg-root-x1.pem \
        -o /usr/local/share/ca-certificates/letsencrypt-stg-root-x1.crt \
    && curl -fsSL https://letsencrypt.org/certs/staging/letsencrypt-stg-root-x2.pem \
        -o /usr/local/share/ca-certificates/letsencrypt-stg-root-x2.crt \
    && update-ca-certificates \
    && apt-get purge -y curl \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*
COPY --from=terraform /bin/terraform /usr/local/bin/terraform
COPY infra/docker/saga-worker/entrypoint.sh /entrypoint.sh
COPY infra/docker/saga-worker/terraform-modules /app/terraform-modules
RUN chmod +x /entrypoint.sh
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["/entrypoint.sh"]
