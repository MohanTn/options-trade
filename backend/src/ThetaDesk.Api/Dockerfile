FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ThetaDesk.sln .
COPY src/ThetaDesk.Domain/ThetaDesk.Domain.csproj src/ThetaDesk.Domain/
COPY src/ThetaDesk.Greeks/ThetaDesk.Greeks.csproj src/ThetaDesk.Greeks/
COPY src/ThetaDesk.Data/ThetaDesk.Data.csproj src/ThetaDesk.Data/
COPY src/ThetaDesk.Api/ThetaDesk.Api.csproj src/ThetaDesk.Api/
COPY src/ThetaDesk.Workers/ThetaDesk.Workers.csproj src/ThetaDesk.Workers/
RUN dotnet restore ThetaDesk.sln
COPY . .
RUN dotnet publish src/ThetaDesk.Api/ThetaDesk.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5085
ENTRYPOINT ["dotnet", "ThetaDesk.Api.dll"]
