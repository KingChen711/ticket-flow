FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/Services/Catalog/TicketFlow.Catalog.Api/TicketFlow.Catalog.Api.csproj src/Services/Catalog/TicketFlow.Catalog.Api/
RUN dotnet restore src/Services/Catalog/TicketFlow.Catalog.Api/TicketFlow.Catalog.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Catalog/TicketFlow.Catalog.Api/ src/Services/Catalog/TicketFlow.Catalog.Api/
RUN dotnet publish src/Services/Catalog/TicketFlow.Catalog.Api/TicketFlow.Catalog.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8002
EXPOSE 8002
ENTRYPOINT ["dotnet", "TicketFlow.Catalog.Api.dll"]
