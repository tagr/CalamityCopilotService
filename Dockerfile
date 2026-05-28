FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["CalamityCopilotService.Api/CalamityCopilotService.Api.csproj", "CalamityCopilotService.Api/"]
COPY ["CalamityCopilotService.ServiceDefaults/CalamityCopilotService.ServiceDefaults.csproj", "CalamityCopilotService.ServiceDefaults/"]
RUN dotnet restore "CalamityCopilotService.Api/CalamityCopilotService.Api.csproj"

COPY . .
WORKDIR "/src/CalamityCopilotService.Api"
RUN dotnet publish "CalamityCopilotService.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CalamityCopilotService.Api.dll"]
