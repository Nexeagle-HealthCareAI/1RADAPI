FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY 1RadAPI/1RadAPI.csproj 1RadAPI/
COPY 1Rad.Application/1Rad.Application.csproj 1Rad.Application/
COPY 1Rad.Domain/1Rad.Domain.csproj 1Rad.Domain/
COPY 1Rad.Infrastructure/1Rad.Infrastructure.csproj 1Rad.Infrastructure/
RUN dotnet restore 1RadAPI/1RadAPI.csproj
COPY . .
WORKDIR /src/1RadAPI
RUN dotnet publish 1RadAPI.csproj -c Release -o /app/publish
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=0 /app/publish .
EXPOSE 80
ENTRYPOINT ["dotnet", "1RadAPI.dll"]
