FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# restore first, from the project file alone: this layer is cached until a dependency changes
COPY DiscordGithubBot.sln .
COPY src/DiscordGithubBot/DiscordGithubBot.csproj src/DiscordGithubBot/
RUN dotnet restore src/DiscordGithubBot
COPY src/ src/
RUN dotnet publish src/DiscordGithubBot -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
# the db volume must be writable by the non-root app user, so chown BEFORE switching to it
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
COPY --from=build /app .
ENV Database__Path=/data/app.db
ENTRYPOINT ["dotnet", "DiscordGithubBot.dll"]
