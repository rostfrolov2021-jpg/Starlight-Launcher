FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src/Starlight.Domain src/Starlight.Domain
COPY src/Starlight.Server src/Starlight.Server
RUN dotnet publish src/Starlight.Server -c Release -o /app
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5088 STARLIGHT_DATA=/data/users.json STARLIGHT_NAT_PORT=5089
EXPOSE 5088 5089/udp
USER $APP_UID
ENTRYPOINT ["dotnet", "Starlight.Server.dll"]
