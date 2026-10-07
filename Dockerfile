FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
COPY src/Nabo.Recipes.csproj src/
RUN dotnet restore src/Nabo.Recipes.csproj
COPY src/ src/
RUN dotnet publish src/Nabo.Recipes.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nabo.Recipes.dll"]
