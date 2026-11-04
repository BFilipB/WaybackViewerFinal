# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY WaybackViewer.slnx ./
COPY src/WaybackViewer/WaybackViewer.csproj src/WaybackViewer/
RUN dotnet restore src/WaybackViewer/WaybackViewer.csproj
COPY src/ src/
RUN dotnet publish src/WaybackViewer/WaybackViewer.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Everything the app writes (SQLite file + compressed HTML payloads) lives in /data: mount a volume there.
ENV ASPNETCORE_URLS=http://+:8080 \
	ConnectionStrings__WaybackDb="Data Source=/data/wayback.db" \
	Storage__HtmlRoot=/data/html-payloads
RUN mkdir -p /data && chown -R $APP_UID /data
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "WaybackViewer.dll"]
