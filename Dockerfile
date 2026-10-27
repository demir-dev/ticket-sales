FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/TicketSales.csproj src/
RUN dotnet restore src/TicketSales.csproj
COPY src/ src/
RUN dotnet publish src/TicketSales.csproj -c Release -o /publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish .
RUN mkdir /data && chown app:app /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "TicketSales.dll"]
