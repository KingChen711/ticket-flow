FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY contracts/protos/reservation.proto contracts/protos/
COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/Services/Reservation/TicketFlow.Reservation.Domain/TicketFlow.Reservation.Domain.csproj src/Services/Reservation/TicketFlow.Reservation.Domain/
COPY src/Services/Reservation/TicketFlow.Reservation.Application/TicketFlow.Reservation.Application.csproj src/Services/Reservation/TicketFlow.Reservation.Application/
COPY src/Services/Reservation/TicketFlow.Reservation.Infrastructure/TicketFlow.Reservation.Infrastructure.csproj src/Services/Reservation/TicketFlow.Reservation.Infrastructure/
COPY src/Services/Reservation/TicketFlow.Reservation.Api/TicketFlow.Reservation.Api.csproj src/Services/Reservation/TicketFlow.Reservation.Api/
RUN dotnet restore src/Services/Reservation/TicketFlow.Reservation.Api/TicketFlow.Reservation.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Reservation/TicketFlow.Reservation.Domain/ src/Services/Reservation/TicketFlow.Reservation.Domain/
COPY src/Services/Reservation/TicketFlow.Reservation.Application/ src/Services/Reservation/TicketFlow.Reservation.Application/
COPY src/Services/Reservation/TicketFlow.Reservation.Infrastructure/ src/Services/Reservation/TicketFlow.Reservation.Infrastructure/
COPY src/Services/Reservation/TicketFlow.Reservation.Api/ src/Services/Reservation/TicketFlow.Reservation.Api/
RUN dotnet publish src/Services/Reservation/TicketFlow.Reservation.Api/TicketFlow.Reservation.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8003
EXPOSE 9003
ENTRYPOINT ["dotnet", "TicketFlow.Reservation.Api.dll"]
