# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY NuGet.config global.json Directory.Build.props Directory.Packages.props ./
COPY ComfyUIMCPSharp.csproj ./
ARG TARGETARCH
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet restore ComfyUIMCPSharp.csproj \
    -r "$rid" \
    -p:PublishSingleFile=true \
    -p:SelfContained=false \
    -p:EnableCompressionInSingleFile=false

COPY . .
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet publish ComfyUIMCPSharp.csproj \
    -c Release \
    --no-restore \
    -r "$rid" \
    --self-contained false \
    -o /app/publish \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:IsTransformWebConfigDisabled=true \
    -p:StaticWebAssetsEnabled=false \
    -p:DebugType=none \
    -p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    COMFYUIMCP_Server__Host=0.0.0.0 \
    COMFYUIMCP_Server__Port=5715 \
    COMFYUIMCP_Server__Path=/mcp \
    COMFYUIMCP_Server__Password= \
    COMFYUIMCP_ComfyUI__ReadOnly=true \
    COMFYUIMCP_ComfyUI__AllowQueueControl=false \
    COMFYUIMCP_ComfyUI__AllowDelete=false \
    COMFYUIMCP_ComfyUI__OutputDirectory=/app/outputs

RUN mkdir -p /app/logs /app/outputs && chown -R $APP_UID:0 /app
COPY --from=build --chown=$APP_UID:0 /app/publish ./

USER $APP_UID
EXPOSE 5715
VOLUME ["/app/logs", "/app/outputs"]

ENTRYPOINT ["./ComfyUIMCPSharp"]
