FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/BuildingBlocks/TicketFlow.Messaging/TicketFlow.Messaging.csproj src/BuildingBlocks/TicketFlow.Messaging/
COPY src/BuildingBlocks/TicketFlow.Contracts.Messaging/TicketFlow.Contracts.Messaging.csproj src/BuildingBlocks/TicketFlow.Contracts.Messaging/
COPY src/Services/Recommendation/TicketFlow.Recommendation.Application/TicketFlow.Recommendation.Application.csproj src/Services/Recommendation/TicketFlow.Recommendation.Application/
COPY src/Services/Recommendation/TicketFlow.Recommendation.Infrastructure/TicketFlow.Recommendation.Infrastructure.csproj src/Services/Recommendation/TicketFlow.Recommendation.Infrastructure/
COPY src/Services/Recommendation/TicketFlow.Recommendation.Api/TicketFlow.Recommendation.Api.csproj src/Services/Recommendation/TicketFlow.Recommendation.Api/
RUN dotnet restore src/Services/Recommendation/TicketFlow.Recommendation.Api/TicketFlow.Recommendation.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Recommendation/TicketFlow.Recommendation.Application/ src/Services/Recommendation/TicketFlow.Recommendation.Application/
COPY src/Services/Recommendation/TicketFlow.Recommendation.Infrastructure/ src/Services/Recommendation/TicketFlow.Recommendation.Infrastructure/
COPY src/Services/Recommendation/TicketFlow.Recommendation.Api/ src/Services/Recommendation/TicketFlow.Recommendation.Api/
RUN dotnet publish src/Services/Recommendation/TicketFlow.Recommendation.Api/TicketFlow.Recommendation.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8009
EXPOSE 8009
ENTRYPOINT ["dotnet", "TicketFlow.Recommendation.Api.dll"]
