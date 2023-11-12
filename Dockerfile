FROM mcr.microsoft.com/dotnet/runtime:7.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["Promul.csproj", "./"]
RUN dotnet restore "Promul.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "Promul.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Promul.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Promul.dll"]
