FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
VOLUME ["/data", "/plugins"]

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AcmeProxy.sln .
COPY src/AcmeProxy/AcmeProxy.csproj src/AcmeProxy/
COPY tests/AcmeProxy.Tests/AcmeProxy.Tests.csproj tests/AcmeProxy.Tests/
RUN dotnet restore

COPY . .
RUN dotnet test tests/AcmeProxy.Tests/AcmeProxy.Tests.csproj --no-restore --configuration Release

RUN dotnet publish src/AcmeProxy/AcmeProxy.csproj \
	--no-restore \
	--configuration Release \
	--output /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AcmeProxy.dll"]
