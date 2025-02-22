FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src/MailZort

COPY *.csproj ./
RUN dotnet restore

# Copy the remaining source code and build the application.
COPY . ./
RUN dotnet publish -c Release -o /app/out

# Build the runtime image.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/out .

# Start the application.
ENTRYPOINT ["dotnet", "MailZort.dll"]
