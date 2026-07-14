FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY contracts/protos/reservation.proto contracts/protos/
COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/BuildingBlocks/TicketFlow.Messaging/TicketFlow.Messaging.csproj src/BuildingBlocks/TicketFlow.Messaging/
COPY src/BuildingBlocks/TicketFlow.Contracts.Messaging/TicketFlow.Contracts.Messaging.csproj src/BuildingBlocks/TicketFlow.Contracts.Messaging/
COPY src/Services/Ordering/TicketFlow.Ordering.Domain/TicketFlow.Ordering.Domain.csproj src/Services/Ordering/TicketFlow.Ordering.Domain/
COPY src/Services/Ordering/TicketFlow.Ordering.Application/TicketFlow.Ordering.Application.csproj src/Services/Ordering/TicketFlow.Ordering.Application/
COPY src/Services/Ordering/TicketFlow.Ordering.Infrastructure/TicketFlow.Ordering.Infrastructure.csproj src/Services/Ordering/TicketFlow.Ordering.Infrastructure/
COPY src/Services/Ordering/TicketFlow.Ordering.Api/TicketFlow.Ordering.Api.csproj src/Services/Ordering/TicketFlow.Ordering.Api/
RUN dotnet restore src/Services/Ordering/TicketFlow.Ordering.Api/TicketFlow.Ordering.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Ordering/TicketFlow.Ordering.Domain/ src/Services/Ordering/TicketFlow.Ordering.Domain/
COPY src/Services/Ordering/TicketFlow.Ordering.Application/ src/Services/Ordering/TicketFlow.Ordering.Application/
COPY src/Services/Ordering/TicketFlow.Ordering.Infrastructure/ src/Services/Ordering/TicketFlow.Ordering.Infrastructure/
COPY src/Services/Ordering/TicketFlow.Ordering.Api/ src/Services/Ordering/TicketFlow.Ordering.Api/
RUN dotnet publish src/Services/Ordering/TicketFlow.Ordering.Api/TicketFlow.Ordering.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8004
EXPOSE 8004
ENTRYPOINT ["dotnet", "TicketFlow.Ordering.Api.dll"]
