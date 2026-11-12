FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

RUN dotnet tool install --tool-path /tools dotnet-trace --version 8.*

COPY src/EnrollmentService/EnrollmentService.csproj ./src/EnrollmentService/
RUN dotnet restore ./src/EnrollmentService/EnrollmentService.csproj

COPY src/EnrollmentService/ ./src/EnrollmentService/
WORKDIR /src/src/EnrollmentService
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /tools /tools
ENV PATH="${PATH}:/tools"
COPY --from=build /app/publish .
COPY migrations/ ./migrations/

EXPOSE 8080
ENTRYPOINT ["dotnet", "EnrollmentService.dll"]
