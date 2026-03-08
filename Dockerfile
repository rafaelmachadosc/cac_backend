FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY AgendamentosApi/AgendamentosApi.csproj AgendamentosApi/
RUN dotnet restore AgendamentosApi/AgendamentosApi.csproj

COPY . .
RUN dotnet publish AgendamentosApi/AgendamentosApi.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgendamentosApi.dll"]
