FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props .
COPY Workbench.Contracts/ Workbench.Contracts/
COPY Workbench.Distributor/ Workbench.Distributor/
RUN dotnet publish Workbench.Distributor/Workbench.Distributor.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
ENV Distributor__Database=/data/distributor.db
RUN mkdir -p /data && chown $APP_UID /data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Workbench.Distributor.dll"]
