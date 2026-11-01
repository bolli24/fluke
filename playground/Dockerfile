# Playground image, see CONTRACT.md in the playground-runner repo.
# Build from the repository root:
#   docker build -f playground/Dockerfile --build-arg REVISION=$(git rev-parse HEAD) -t fluke-playground .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# The compiler starts once per job, so ReadyToRun saves the JIT warm-up every time. Self-contained
# with a composite image precompiles the framework code fluke uses too, which roughly halves compile
# times under gVisor, where JIT and type loading are slow.
RUN dotnet publish fluke.csproj -c Release -r linux-x64 --self-contained true \
        -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true -o /out/fluke

# The browser linter: fluke.Wasm, which needs the wasm-tools workload
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS linter
# The workload relinks the runtime with Emscripten, whose tools are Python scripts
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/* \
    && dotnet workload install wasm-tools
WORKDIR /src
COPY . .
# The runner serves files as they are, so drop the precompressed copies
RUN dotnet publish fluke.Wasm -c Release -o /out/wasm \
    && find /out/wasm/wwwroot/_framework \( -name '*.br' -o -name '*.gz' \) -delete

FROM mcr.microsoft.com/dotnet/runtime:10.0
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_EnableDiagnostics=0

COPY --from=build /out/fluke /opt/fluke
RUN printf '#!/bin/sh\nexec /opt/fluke/fluke "$@"\n' > /usr/local/bin/fluke \
    && chmod 755 /usr/local/bin/fluke
COPY --chmod=755 playground/run.sh /playground/run
COPY playground/fixtures /playground/fixtures
# Examples are the test programs; examples.toml picks which ones are shown
COPY test /playground/examples
COPY playground/examples.toml /playground/examples/examples.toml
COPY --from=linter /out/wasm/wwwroot/_framework /playground/linter/_framework
COPY playground/linter/index.js /playground/linter/index.js
# A Windows checkout has CRLF line endings, which break the shebang
RUN sed -i 's/\r$//' /playground/run

ARG REVISION=unknown
LABEL playground.contract="1" \
      playground.language="fluke" \
      playground.extensions=".fl" \
      playground.capabilities="" \
      playground.views="hir,mir" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.source="https://github.com/bolli24/fluke"

USER 65534:65534
ENTRYPOINT ["/playground/run"]
