# UmByul Admin (Blazor Server) — 지속 WebSocket 필요(상시 가동). arm64 네이티브 빌드.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Admin.csproj ./
RUN dotnet restore Admin.csproj
COPY . ./
RUN dotnet publish Admin.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Admin.dll"]
