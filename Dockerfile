FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/EnrollmentService/EnrollmentService.csproj ./src/EnrollmentService/
RUN dotnet restore ./src/EnrollmentService/EnrollmentService.csproj

COPY src/EnrollmentService/ ./src/EnrollmentService/
WORKDIR /src/src/EnrollmentService
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY migrations/ ./migrations/

EXPOSE 8080
ENTRYPOINT ["dotnet", "EnrollmentService.dll"]
