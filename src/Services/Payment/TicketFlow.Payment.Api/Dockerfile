FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/BuildingBlocks/TicketFlow.Messaging/TicketFlow.Messaging.csproj src/BuildingBlocks/TicketFlow.Messaging/
COPY src/BuildingBlocks/TicketFlow.Contracts.Messaging/TicketFlow.Contracts.Messaging.csproj src/BuildingBlocks/TicketFlow.Contracts.Messaging/
COPY src/Services/Payment/TicketFlow.Payment.Domain/TicketFlow.Payment.Domain.csproj src/Services/Payment/TicketFlow.Payment.Domain/
COPY src/Services/Payment/TicketFlow.Payment.Application/TicketFlow.Payment.Application.csproj src/Services/Payment/TicketFlow.Payment.Application/
COPY src/Services/Payment/TicketFlow.Payment.Infrastructure/TicketFlow.Payment.Infrastructure.csproj src/Services/Payment/TicketFlow.Payment.Infrastructure/
COPY src/Services/Payment/TicketFlow.Payment.Api/TicketFlow.Payment.Api.csproj src/Services/Payment/TicketFlow.Payment.Api/
RUN dotnet restore src/Services/Payment/TicketFlow.Payment.Api/TicketFlow.Payment.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Payment/TicketFlow.Payment.Domain/ src/Services/Payment/TicketFlow.Payment.Domain/
COPY src/Services/Payment/TicketFlow.Payment.Application/ src/Services/Payment/TicketFlow.Payment.Application/
COPY src/Services/Payment/TicketFlow.Payment.Infrastructure/ src/Services/Payment/TicketFlow.Payment.Infrastructure/
COPY src/Services/Payment/TicketFlow.Payment.Api/ src/Services/Payment/TicketFlow.Payment.Api/
RUN dotnet publish src/Services/Payment/TicketFlow.Payment.Api/TicketFlow.Payment.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8007
EXPOSE 8007
ENTRYPOINT ["dotnet", "TicketFlow.Payment.Api.dll"]
