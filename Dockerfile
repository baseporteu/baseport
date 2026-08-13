FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0-preview-alpine AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json ./
COPY Source/Directory.Build.props Source/Directory.Packages.props Source/
COPY Source/Baseport/Baseport.csproj Source/Baseport/

RUN RID="linux-musl-$(case "$TARGETARCH" in amd64) echo x64 ;; *) echo "$TARGETARCH" ;; esac)" \
    && dotnet restore Source/Baseport/Baseport.csproj -r "$RID"

COPY Source/Baseport/ Source/Baseport/

RUN RID="linux-musl-$(case "$TARGETARCH" in amd64) echo x64 ;; *) echo "$TARGETARCH" ;; esac)" \
    && dotnet publish Source/Baseport/Baseport.csproj -c Release -r "$RID" -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-preview-alpine AS runtime
WORKDIR /app

RUN addgroup -S baseport && adduser -S -G baseport baseport

COPY --from=build --chown=baseport:baseport /app/publish/Baseport ./Baseport

RUN mkdir -p /data && chown baseport:baseport /data
USER baseport

ENV Baseport__ConnectionString="Data Source=/data/baseport.db" \
    ASPNETCORE_URLS="http://+:5263"
WORKDIR /data

EXPOSE 5263
VOLUME ["/data"]

ENTRYPOINT ["/app/Baseport"]
