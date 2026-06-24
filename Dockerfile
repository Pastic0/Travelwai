# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY TravelwAI.AspNetCore.sln ./
COPY src/TravelwAI.Models/TravelwAI.Models.csproj src/TravelwAI.Models/
COPY src/TravelwAI.Data/TravelwAI.Data.csproj src/TravelwAI.Data/
COPY src/TravelwAI.Business/TravelwAI.Business.csproj src/TravelwAI.Business/
COPY src/TravelwAI.Web/TravelwAI.Web.csproj src/TravelwAI.Web/

RUN dotnet restore TravelwAI.AspNetCore.sln

COPY . .
RUN dotnet publish src/TravelwAI.Web/TravelwAI.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV DOTNET_EnableDiagnostics=0
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["sh", "-c", "dotnet TravelwAI.Web.dll --urls http://0.0.0.0:${PORT:-8080}"]
