FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/BuildingBlocks/TicketFlow.Messaging/TicketFlow.Messaging.csproj src/BuildingBlocks/TicketFlow.Messaging/
COPY src/BuildingBlocks/TicketFlow.Contracts.Messaging/TicketFlow.Contracts.Messaging.csproj src/BuildingBlocks/TicketFlow.Contracts.Messaging/
COPY src/Services/Ticketing/TicketFlow.Ticketing.Domain/TicketFlow.Ticketing.Domain.csproj src/Services/Ticketing/TicketFlow.Ticketing.Domain/
COPY src/Services/Ticketing/TicketFlow.Ticketing.Application/TicketFlow.Ticketing.Application.csproj src/Services/Ticketing/TicketFlow.Ticketing.Application/
COPY src/Services/Ticketing/TicketFlow.Ticketing.Infrastructure/TicketFlow.Ticketing.Infrastructure.csproj src/Services/Ticketing/TicketFlow.Ticketing.Infrastructure/
COPY src/Services/Ticketing/TicketFlow.Ticketing.Api/TicketFlow.Ticketing.Api.csproj src/Services/Ticketing/TicketFlow.Ticketing.Api/
RUN dotnet restore src/Services/Ticketing/TicketFlow.Ticketing.Api/TicketFlow.Ticketing.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Ticketing/TicketFlow.Ticketing.Domain/ src/Services/Ticketing/TicketFlow.Ticketing.Domain/
COPY src/Services/Ticketing/TicketFlow.Ticketing.Application/ src/Services/Ticketing/TicketFlow.Ticketing.Application/
COPY src/Services/Ticketing/TicketFlow.Ticketing.Infrastructure/ src/Services/Ticketing/TicketFlow.Ticketing.Infrastructure/
COPY src/Services/Ticketing/TicketFlow.Ticketing.Api/ src/Services/Ticketing/TicketFlow.Ticketing.Api/
RUN dotnet publish src/Services/Ticketing/TicketFlow.Ticketing.Api/TicketFlow.Ticketing.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8008
EXPOSE 8008
ENTRYPOINT ["dotnet", "TicketFlow.Ticketing.Api.dll"]
