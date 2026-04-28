# ============================================
# ESTÁGIO 1: BUILD
# ============================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copia o arquivo de projeto e restaura as dependências
COPY MeusApp.csproj .
RUN dotnet restore

# Copia todo o restante do código
COPY . .

# Publica a aplicação em modo Release
RUN dotnet publish -c Release -o /app/publish

# ============================================
# ESTÁGIO 2: RUNTIME
# ============================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Instala dependências nativas necessárias para SkiaSharp e ONNX Runtime
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    libc6-dev \
    libgdiplus \
    libx11-dev \
    libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*

# Copia os arquivos publicados do estágio de build
COPY --from=build /app/publish .

# Expõe a porta que o Vercel vai definir via variável PORT
EXPOSE 8080

# Define o entrypoint
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "MeusApp.dll"]
