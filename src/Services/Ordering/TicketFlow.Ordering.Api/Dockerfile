FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY contracts/protos/reservation.proto contracts/protos/
COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/Services/Ordering/TicketFlow.Ordering.Api/TicketFlow.Ordering.Api.csproj src/Services/Ordering/TicketFlow.Ordering.Api/
RUN dotnet restore src/Services/Ordering/TicketFlow.Ordering.Api/TicketFlow.Ordering.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Ordering/TicketFlow.Ordering.Api/ src/Services/Ordering/TicketFlow.Ordering.Api/
RUN dotnet publish src/Services/Ordering/TicketFlow.Ordering.Api/TicketFlow.Ordering.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8004
EXPOSE 8004
ENTRYPOINT ["dotnet", "TicketFlow.Ordering.Api.dll"]
