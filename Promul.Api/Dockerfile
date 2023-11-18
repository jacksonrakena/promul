FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["Promul.Api/Promul.Api.csproj", "Promul.Api/"]
RUN dotnet restore "Promul.Api/Promul.Api.csproj"
COPY . .
WORKDIR "/src/Promul.Api"
RUN dotnet build "Promul.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Promul.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Promul.Api.dll"]
