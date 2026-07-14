FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/BuildingBlocks/TicketFlow.Messaging/TicketFlow.Messaging.csproj src/BuildingBlocks/TicketFlow.Messaging/
COPY src/BuildingBlocks/TicketFlow.Contracts.Messaging/TicketFlow.Contracts.Messaging.csproj src/BuildingBlocks/TicketFlow.Contracts.Messaging/
COPY src/Services/Search/TicketFlow.Search.Api/TicketFlow.Search.Api.csproj src/Services/Search/TicketFlow.Search.Api/
RUN dotnet restore src/Services/Search/TicketFlow.Search.Api/TicketFlow.Search.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Search/TicketFlow.Search.Api/ src/Services/Search/TicketFlow.Search.Api/
RUN dotnet publish src/Services/Search/TicketFlow.Search.Api/TicketFlow.Search.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8006
EXPOSE 8006
ENTRYPOINT ["dotnet", "TicketFlow.Search.Api.dll"]
