# UmByul Api (.NET 10 minimal API) — arm64(OCI A1)에서 빌드 시 네이티브 arm64 이미지.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Api.csproj ./
RUN dotnet restore Api.csproj
COPY . ./
RUN dotnet publish Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Api.dll"]
