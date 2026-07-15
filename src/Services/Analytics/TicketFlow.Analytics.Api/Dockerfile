FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/BuildingBlocks/TicketFlow.Messaging/TicketFlow.Messaging.csproj src/BuildingBlocks/TicketFlow.Messaging/
COPY src/BuildingBlocks/TicketFlow.Contracts.Messaging/TicketFlow.Contracts.Messaging.csproj src/BuildingBlocks/TicketFlow.Contracts.Messaging/
COPY src/Services/Analytics/TicketFlow.Analytics.Application/TicketFlow.Analytics.Application.csproj src/Services/Analytics/TicketFlow.Analytics.Application/
COPY src/Services/Analytics/TicketFlow.Analytics.Infrastructure/TicketFlow.Analytics.Infrastructure.csproj src/Services/Analytics/TicketFlow.Analytics.Infrastructure/
COPY src/Services/Analytics/TicketFlow.Analytics.Api/TicketFlow.Analytics.Api.csproj src/Services/Analytics/TicketFlow.Analytics.Api/
RUN dotnet restore src/Services/Analytics/TicketFlow.Analytics.Api/TicketFlow.Analytics.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Analytics/TicketFlow.Analytics.Application/ src/Services/Analytics/TicketFlow.Analytics.Application/
COPY src/Services/Analytics/TicketFlow.Analytics.Infrastructure/ src/Services/Analytics/TicketFlow.Analytics.Infrastructure/
COPY src/Services/Analytics/TicketFlow.Analytics.Api/ src/Services/Analytics/TicketFlow.Analytics.Api/
RUN dotnet publish src/Services/Analytics/TicketFlow.Analytics.Api/TicketFlow.Analytics.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8010
EXPOSE 8010
ENTRYPOINT ["dotnet", "TicketFlow.Analytics.Api.dll"]
