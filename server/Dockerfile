# Builds either server application from the repository root:
#   docker build -f server/Dockerfile --build-arg APP=DownholeStudio.LicenseServer -t quakelogic/dhs-license-server .
#   docker build -f server/Dockerfile --build-arg APP=DownholeStudio.AdminPortal   -t quakelogic/dhs-admin-portal .
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG APP
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/DownholeStudio.Licensing/ src/DownholeStudio.Licensing/
COPY server/DownholeStudio.Server.Data/ server/DownholeStudio.Server.Data/
COPY server/${APP}/ server/${APP}/
RUN dotnet restore server/${APP}/${APP}.csproj
RUN dotnet publish server/${APP}/${APP}.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG APP
ENV APP_DLL=${APP}.dll \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=0
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\" \"$@\"", "--"]
