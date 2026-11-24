FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Library.Domain/Library.Domain.csproj", "Library.Domain/"]
COPY ["src/Library.Infrastructure/Library.Infrastructure.csproj", "Library.Infrastructure/"]
COPY ["src/Library.Worker/Library.Worker.csproj", "Library.Worker/"]

RUN dotnet restore "Library.Worker/Library.Worker.csproj"

COPY src/Library.Domain/ Library.Domain/
COPY src/Library.Infrastructure/ Library.Infrastructure/
COPY src/Library.Worker/ Library.Worker/

WORKDIR /src/Library.Worker
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Library.Worker.dll"]
