FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Both project files first, so restore is cached independently of the source.
COPY ["MailZort/MailZort.csproj", "MailZort/"]
COPY ["RoboDodd.OrmLite/src/RoboDodd.OrmLite/RoboDodd.OrmLite.csproj", "RoboDodd.OrmLite/src/RoboDodd.OrmLite/"]

RUN dotnet restore "./MailZort/MailZort.csproj"

# Copy the remaining source code and build the application.
COPY . .
WORKDIR "/src/MailZort"
RUN dotnet publish -c Release -o /app/out

# Build the runtime image. ASP.NET runtime, not the plain one - this hosts the portal.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/out .

# The portal listens here; the mail loop runs in the same process.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Sender reputation, rules, history and logins live here - mount a volume.
VOLUME ["/app/data"]

# Start the application.
ENTRYPOINT ["dotnet", "MailZort.dll"]
