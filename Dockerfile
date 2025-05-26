FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["InsuareBot/InsuareBot.csproj", "InsuareBot/"]
RUN dotnet restore "InsuareBot/InsuareBot.csproj"
COPY . .
WORKDIR "/src/InsuareBot"
RUN dotnet build "InsuareBot.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "InsuareBot.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "InsuareBot.dll"]