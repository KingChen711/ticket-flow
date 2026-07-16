FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/Gateway/TicketFlow.Gateway/TicketFlow.Gateway.csproj src/Gateway/TicketFlow.Gateway/
RUN dotnet restore src/Gateway/TicketFlow.Gateway/TicketFlow.Gateway.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Gateway/TicketFlow.Gateway/ src/Gateway/TicketFlow.Gateway/
RUN dotnet publish src/Gateway/TicketFlow.Gateway/TicketFlow.Gateway.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TicketFlow.Gateway.dll"]
