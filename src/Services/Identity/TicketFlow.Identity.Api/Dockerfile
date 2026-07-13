FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/BuildingBlocks/TicketFlow.Domain/TicketFlow.Domain.csproj src/BuildingBlocks/TicketFlow.Domain/
COPY src/BuildingBlocks/TicketFlow.Web/TicketFlow.Web.csproj src/BuildingBlocks/TicketFlow.Web/
COPY src/Services/Identity/TicketFlow.Identity.Domain/TicketFlow.Identity.Domain.csproj src/Services/Identity/TicketFlow.Identity.Domain/
COPY src/Services/Identity/TicketFlow.Identity.Application/TicketFlow.Identity.Application.csproj src/Services/Identity/TicketFlow.Identity.Application/
COPY src/Services/Identity/TicketFlow.Identity.Infrastructure/TicketFlow.Identity.Infrastructure.csproj src/Services/Identity/TicketFlow.Identity.Infrastructure/
COPY src/Services/Identity/TicketFlow.Identity.Api/TicketFlow.Identity.Api.csproj src/Services/Identity/TicketFlow.Identity.Api/
RUN dotnet restore src/Services/Identity/TicketFlow.Identity.Api/TicketFlow.Identity.Api.csproj

COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Identity/TicketFlow.Identity.Domain/ src/Services/Identity/TicketFlow.Identity.Domain/
COPY src/Services/Identity/TicketFlow.Identity.Application/ src/Services/Identity/TicketFlow.Identity.Application/
COPY src/Services/Identity/TicketFlow.Identity.Infrastructure/ src/Services/Identity/TicketFlow.Identity.Infrastructure/
COPY src/Services/Identity/TicketFlow.Identity.Api/ src/Services/Identity/TicketFlow.Identity.Api/
RUN dotnet publish src/Services/Identity/TicketFlow.Identity.Api/TicketFlow.Identity.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8001
EXPOSE 8001
ENTRYPOINT ["dotnet", "TicketFlow.Identity.Api.dll"]
