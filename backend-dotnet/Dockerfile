# SmartPLC — imagem para o Render (runtime Docker). Contexto de build: esta pasta (backend-dotnet/).
# NÃO foi construída nesta máquina (sem Docker em execução): valide com `docker build` antes do deploy.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/SmartPlc.Api/SmartPlc.Api.csproj src/SmartPlc.Api/
RUN dotnet restore src/SmartPlc.Api/SmartPlc.Api.csproj
COPY src/SmartPlc.Api/ src/SmartPlc.Api/
RUN dotnet publish src/SmartPlc.Api/SmartPlc.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# O Render informa a porta em PORT (padrão 10000); Program.cs escuta em 0.0.0.0:$PORT.
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 10000
ENTRYPOINT ["dotnet", "SmartPlc.Api.dll"]
