ARG GRANITE_HF_REPO=ibm-granite/granite-embedding-311m-multilingual-r2
ARG GRANITE_REVISION=main

FROM python:3.12-slim AS granite
ARG GRANITE_HF_REPO
ARG GRANITE_REVISION
RUN pip install --no-cache-dir "huggingface_hub[cli]"
RUN hf download "$GRANITE_HF_REPO" \
        "onnx/model.onnx" "tokenizer.json" "config.json" \
        --revision "$GRANITE_REVISION" \
        --local-dir /tmp/granite

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
    wget \
    ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY --from=granite /tmp/granite/onnx/model.onnx     /app/models/granite/model.onnx
COPY --from=granite /tmp/granite/tokenizer.json      /app/models/granite/tokenizer.json
COPY --from=granite /tmp/granite/config.json         /app/models/granite/config.json
EXPOSE 8080
ENTRYPOINT ["dotnet", "ThanyMarcus.Cloud.Api.dll"]
