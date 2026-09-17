# The web app as a container image, so every machine runs identical bytes
# whatever its OS or CPU. The catalogue is NOT baked in: it is 58 MB and the
# collector republishes it every half hour, so it is mounted from the host
# read-only instead (see docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so a change to source does not invalidate the restore layer.
COPY CoursePlanner.slnx ./
COPY CoursePlanner.Data/CoursePlanner.Data.csproj CoursePlanner.Data/
COPY Web/Web.csproj Web/
RUN dotnet restore Web/Web.csproj

COPY CoursePlanner.Data/ CoursePlanner.Data/
COPY Web/ Web/
RUN dotnet publish Web/Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./

# Kestrel listens inside the container; compose maps it to localhost only, so
# the Cloudflare tunnel stays the single way in from outside.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# Runs unprivileged: the image needs no write access to anything.
USER $APP_UID

ENTRYPOINT ["dotnet", "Web.dll"]
