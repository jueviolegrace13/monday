# Monday API — runs on Render's free web service.

# The viewer (Angular) builds into src/Monday.Api/wwwroot; its styles import design/tokens.css.
FROM node:22 AS viewer
WORKDIR /src/viewer/app
COPY viewer/app/package.json viewer/app/package-lock.json ./
RUN npm ci
COPY viewer/app/ ./
COPY design/ /src/design/
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=viewer /src/src/Monday.Api/wwwroot src/Monday.Api/wwwroot
RUN dotnet publish src/Monday.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Monday.Api.dll"]
