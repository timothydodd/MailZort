FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /app

COPY *.csproj ./
RUN dotnet restore

# Copy the remaining source code and build the application.
COPY . ./
RUN dotnet publish -c Release -o out

# Build the runtime image.
FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS runtime
WORKDIR /app
COPY --from=build /app/out ./


# Start the application.
ENTRYPOINT ["dotnet", "MailZort.dll"]