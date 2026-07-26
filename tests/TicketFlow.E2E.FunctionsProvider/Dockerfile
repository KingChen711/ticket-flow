FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/Functions/TicketFlow.Functions/TicketFlow.Functions.csproj src/Functions/TicketFlow.Functions/
COPY src/BuildingBlocks/TicketFlow.Messaging/TicketFlow.Messaging.csproj src/BuildingBlocks/TicketFlow.Messaging/
COPY src/BuildingBlocks/TicketFlow.Contracts.Messaging/TicketFlow.Contracts.Messaging.csproj src/BuildingBlocks/TicketFlow.Contracts.Messaging/
COPY tests/TicketFlow.E2E.FunctionsProvider/TicketFlow.E2E.FunctionsProvider.csproj tests/TicketFlow.E2E.FunctionsProvider/
RUN dotnet restore tests/TicketFlow.E2E.FunctionsProvider/TicketFlow.E2E.FunctionsProvider.csproj

COPY src/Functions/TicketFlow.Functions/ src/Functions/TicketFlow.Functions/
COPY src/BuildingBlocks/TicketFlow.Messaging/ src/BuildingBlocks/TicketFlow.Messaging/
COPY src/BuildingBlocks/TicketFlow.Contracts.Messaging/ src/BuildingBlocks/TicketFlow.Contracts.Messaging/
COPY tests/TicketFlow.E2E.FunctionsProvider/ tests/TicketFlow.E2E.FunctionsProvider/
RUN dotnet publish tests/TicketFlow.E2E.FunctionsProvider/TicketFlow.E2E.FunctionsProvider.csproj \
    --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TicketFlow.E2E.FunctionsProvider.dll"]
