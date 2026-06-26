# syntax=docker/dockerfile:1

# ---- restore (cached on csproj only) ----------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
COPY src/AcmeProxy/AcmeProxy.csproj src/AcmeProxy/
RUN dotnet restore src/AcmeProxy/AcmeProxy.csproj

# ---- build + publish --------------------------------------------------------
FROM restore AS publish
COPY src/AcmeProxy/ src/AcmeProxy/
RUN dotnet publish src/AcmeProxy/AcmeProxy.csproj \
	--no-restore \
	--configuration Release \
	--output /app/publish

# ---- runtime ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 80
VOLUME ["/data"]
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AcmeProxy.dll"]
