FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/BuildingBlocks/TicketFlow.Messaging/TicketFlow.Messaging.csproj src/BuildingBlocks/TicketFlow.Messaging/
COPY src/BuildingBlocks/TicketFlow.Contracts.Messaging/TicketFlow.Contracts.Messaging.csproj src/BuildingBlocks/TicketFlow.Contracts.Messaging/
COPY src/Services/Notification/TicketFlow.Notification.Application/TicketFlow.Notification.Application.csproj src/Services/Notification/TicketFlow.Notification.Application/
COPY src/Services/Notification/TicketFlow.Notification.Infrastructure/TicketFlow.Notification.Infrastructure.csproj src/Services/Notification/TicketFlow.Notification.Infrastructure/
COPY src/Services/Notification/TicketFlow.Notification.Api/TicketFlow.Notification.Api.csproj src/Services/Notification/TicketFlow.Notification.Api/
RUN dotnet restore src/Services/Notification/TicketFlow.Notification.Api/TicketFlow.Notification.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Notification/TicketFlow.Notification.Application/ src/Services/Notification/TicketFlow.Notification.Application/
COPY src/Services/Notification/TicketFlow.Notification.Infrastructure/ src/Services/Notification/TicketFlow.Notification.Infrastructure/
COPY src/Services/Notification/TicketFlow.Notification.Api/ src/Services/Notification/TicketFlow.Notification.Api/
RUN dotnet publish src/Services/Notification/TicketFlow.Notification.Api/TicketFlow.Notification.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8005
EXPOSE 8005
ENTRYPOINT ["dotnet", "TicketFlow.Notification.Api.dll"]
