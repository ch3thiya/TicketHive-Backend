# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["services/BuildingBlocks/BuildingBlocks.csproj", "services/BuildingBlocks/"]
COPY ["services/Booking/Booking.Service.csproj", "services/Booking/"]
RUN dotnet restore "services/Booking/Booking.Service.csproj"

# Copy source code and publish release artifacts
COPY . .
RUN dotnet publish "services/Booking/Booking.Service.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Expose standard container port
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Booking.Service.dll"]