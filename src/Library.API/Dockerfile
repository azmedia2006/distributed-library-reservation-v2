FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Library.Domain/Library.Domain.csproj", "Library.Domain/"]
COPY ["src/Library.Infrastructure/Library.Infrastructure.csproj", "Library.Infrastructure/"]
COPY ["src/Library.API/Library.API.csproj", "Library.API/"]

RUN dotnet restore "Library.API/Library.API.csproj"

COPY src/Library.Domain/ Library.Domain/
COPY src/Library.Infrastructure/ Library.Infrastructure/
COPY src/Library.API/ Library.API/

WORKDIR /src/Library.API
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "Library.API.dll"]
