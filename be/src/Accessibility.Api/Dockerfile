FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Accessibility.Api/Accessibility.Api.csproj", "src/Accessibility.Api/"]
COPY ["src/Accessibility.Infrastructure/Accessibility.Infrastructure.csproj", "src/Accessibility.Infrastructure/"]
COPY ["src/Accessibility.Application/Accessibility.Application.csproj", "src/Accessibility.Application/"]
COPY ["src/Accessibility.Domain/Accessibility.Domain.csproj", "src/Accessibility.Domain/"]
COPY ["Accessibility.slnx", "./"]
RUN dotnet restore "src/Accessibility.Api/Accessibility.Api.csproj"
COPY . .
RUN dotnet publish "src/Accessibility.Api/Accessibility.Api.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Accessibility.Api.dll"]
