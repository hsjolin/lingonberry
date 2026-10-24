FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Lingonberry/Lingonberry.csproj ./Lingonberry/
RUN dotnet restore ./Lingonberry/Lingonberry.csproj
COPY src/Lingonberry/ ./Lingonberry/
RUN dotnet publish ./Lingonberry/Lingonberry.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
# wireguard-tools: `wg genkey`/`wg pubkey` are used when provisioning app VPNs
RUN apk add --no-cache wireguard-tools
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Lingonberry.dll"]
