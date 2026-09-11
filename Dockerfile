FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["TaskForge.sln", "global.json", "Directory.Build.props", "./"]
COPY ["src/TaskForge.Domain/TaskForge.Domain.csproj", "src/TaskForge.Domain/"]
COPY ["src/TaskForge.Application/TaskForge.Application.csproj", "src/TaskForge.Application/"]
COPY ["src/TaskForge.Infrastructure/TaskForge.Infrastructure.csproj", "src/TaskForge.Infrastructure/"]
COPY ["src/TaskForge.Api/TaskForge.Api.csproj", "src/TaskForge.Api/"]
COPY ["src/TaskForge.Debugging/TaskForge.Debugging.csproj", "src/TaskForge.Debugging/"]
COPY ["tests/TaskForge.UnitTests/TaskForge.UnitTests.csproj", "tests/TaskForge.UnitTests/"]
RUN dotnet restore TaskForge.sln

COPY . .
RUN dotnet publish TaskForge.sln --configuration Release --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV URLS=http://+:8080
EXPOSE 8080
COPY --from=build /src/src/TaskForge.Api/bin/Release/net8.0/publish/ ./
USER app
ENTRYPOINT ["dotnet", "TaskForge.Api.dll"]
