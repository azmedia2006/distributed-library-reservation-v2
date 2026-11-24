FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

COPY LibraryDistributedSystem.slnx ./
COPY src/Library.Domain/Library.Domain.csproj src/Library.Domain/
COPY src/Library.ConsoleClient/Library.ConsoleClient.csproj src/Library.ConsoleClient/
RUN dotnet restore src/Library.ConsoleClient/Library.ConsoleClient.csproj

COPY src/Library.Domain/ src/Library.Domain/
COPY src/Library.ConsoleClient/ src/Library.ConsoleClient/
RUN dotnet publish src/Library.ConsoleClient/Library.ConsoleClient.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Library.ConsoleClient.dll"]
