FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["global.json", "Directory.Build.props", "./"]
COPY ["src/TaskForge.Domain/TaskForge.Domain.csproj", "src/TaskForge.Domain/"]
COPY ["src/TaskForge.Application/TaskForge.Application.csproj", "src/TaskForge.Application/"]
COPY ["src/TaskForge.Infrastructure/TaskForge.Infrastructure.csproj", "src/TaskForge.Infrastructure/"]
COPY ["src/TaskForge.Api/TaskForge.Api.csproj", "src/TaskForge.Api/"]
RUN dotnet restore src/TaskForge.Api/TaskForge.Api.csproj

COPY ["src/TaskForge.Domain/", "src/TaskForge.Domain/"]
COPY ["src/TaskForge.Application/", "src/TaskForge.Application/"]
COPY ["src/TaskForge.Infrastructure/", "src/TaskForge.Infrastructure/"]
COPY ["src/TaskForge.Api/", "src/TaskForge.Api/"]
RUN dotnet publish src/TaskForge.Api/TaskForge.Api.csproj --configuration Release --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV URLS=http://+:8080
EXPOSE 8080
COPY --from=build /src/src/TaskForge.Api/bin/Release/net10.0/publish/ ./
USER app
ENTRYPOINT ["dotnet", "TaskForge.Api.dll"]
