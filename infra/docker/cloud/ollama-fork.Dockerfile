# CPU-only build of tc-mb/ollama (MiniCPM-V branch). The fork's own
# Dockerfile bases every flavor on rocm/dev-almalinux-8:7.2.1-complete,
# which exhausts GitHub Actions' disk. This bypass uses debian-slim +
# golang and only builds the CPU ggml backend.
#
# Build context is the cloned fork source — see ollama-fork-ci.yml.

FROM debian:12-slim AS cpu-build
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake ninja-build ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY CMakeLists.txt CMakePresets.json ./
COPY ml/backend/ggml/ggml ml/backend/ggml/ggml
ENV CMAKE_GENERATOR=Ninja
ENV LDFLAGS=-s
RUN cmake --preset CPU \
 && cmake --build --preset CPU -- -l $(nproc) \
 && cmake --install build --component CPU --strip

FROM golang:1.26-bookworm AS go-build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
ENV CGO_ENABLED=1
RUN go build -trimpath -buildmode=pie -ldflags="-w -s" -o /bin/ollama .

FROM ubuntu:24.04
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates libopenblas0 wget \
    && rm -rf /var/lib/apt/lists/*
COPY --from=cpu-build /src/dist/lib/ollama /usr/lib/ollama
COPY --from=go-build  /bin/ollama          /usr/bin/ollama
ENV LD_LIBRARY_PATH=/usr/lib/ollama
ENV OLLAMA_HOST=0.0.0.0:11434
EXPOSE 11434
ENTRYPOINT ["/usr/bin/ollama"]
CMD ["serve"]
