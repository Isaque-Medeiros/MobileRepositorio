# Usa a imagem oficial do .NET 8 SDK para build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copia o arquivo de projeto e restaura as dependências
COPY *.csproj ./
RUN dotnet restore

# Copia todo o código e faz o build
COPY . ./
RUN dotnet publish -c Release -o out

# Imagem runtime menor para produção
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Instala dependências necessárias para o SkiaSharp no Linux
RUN apt-get update && \
    apt-get install -y libfontconfig1 libfreetype6 libharfbuzz0b libpng16-16 && \
    rm -rf /var/lib/apt/lists/*

# Copia o build da etapa anterior
COPY --from=build /app/out .

# Expõe a porta que o Render vai usar
EXPOSE 8080

# Define a variável de ambiente para produção
ENV ASPNETCORE_ENVIRONMENT=Production

# Inicia a aplicação
ENTRYPOINT ["dotnet", "MeusApp.dll"]
