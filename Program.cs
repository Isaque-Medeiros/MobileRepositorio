using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using ClassesBSFM;
using PonteBanco;
using System.Linq;
using BSFM.Services; 
using Microsoft.AspNetCore.Http;
using System.IO;
using Microsoft.AspNetCore.Mvc;

// Correção para trabalhar com datas no PostgreSQL (comum no Railway/Neon)
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Configuração da Porta para o Vercel (usa a variável PORT fornecida pela plataforma)
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
Console.WriteLine($"[INIT] Iniciando servidor na porta {port}");
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Configurações de Serviços
builder.Services.AddCors(options => {
    options.AddPolicy("PermitirSite", policy => 
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddHostedService<LimpezaAnalisesService>(); 
builder.Services.AddSingleton<BSFM.Services.YoloInferenceService>();
builder.Services.AddHttpClient<BSFM.Services.UsdaNutritionService>();
builder.Services.AddDbContext<PonteDB>();

var app = builder.Build();
using (var scope = app.Services.CreateScope()) {
    // AQUI: Mude para PonteDB
    var db = scope.ServiceProvider.GetRequiredService<PonteDB>(); 
    Console.WriteLine("[POSTGRES] Garantindo criação de tabelas...");
    db.Database.EnsureCreated();
}

app.UseCors("PermitirSite");

// --- COMANDOS PARA O SITE FUNCIONAR ---
app.UseDefaultFiles(); // Faz o sistema procurar pelo index.html ou login.html automaticamente
app.UseStaticFiles();  // Importante: Entrega arquivos dentro da pasta 'wwwroot'

// Rota para a Página Inicial (Fallback caso o DefaultFiles não pegue)
app.MapGet("/", (IWebHostEnvironment env) => 
    Results.File(Path.Combine(env.WebRootPath ?? "wwwroot", "index.html"), "text/html"));

// --- SUAS ROTAS DE API ---

app.MapPost("/solicitar-codigo", (SolicitacaoEmail req) => {
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PonteDB>();
    var email = req.Email.Trim().ToLower();
    
    if (db.Usuarios.AsNoTracking().Any(u => u.Email.ToLower() == email))
        return Results.Json(new { mensagem = "E-mail já cadastrado!" }, statusCode: 400);

    // 2. Gera o Token de 6 dígitos
    string token = new Random().Next(100000, 999999).ToString();

    // 3. CHAMA O SERVIÇO DE E-MAIL (Aqui ele envia para o Mailtrap)
    EmailService.EnviarToken(email, token);
    return Results.Ok(new { mensagem = "Código enviado!", tokenParaJs = token });
});

app.MapPost("/cadastrar-usuario-final", (Usuario usuarioVindoDoJs) => {
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PonteDB>();
    // Cuidado: Certifique-se que o pacote BCrypt.Net-Next está no .csproj
    usuarioVindoDoJs.SenhaHash = BCrypt.Net.BCrypt.HashPassword(usuarioVindoDoJs.SenhaHash);
    usuarioVindoDoJs.EmailVerificado = true; 
    new CalcularNutricional().RegistrarCalculos(usuarioVindoDoJs);
    db.Usuarios.Add(usuarioVindoDoJs);
    db.SaveChanges();
    return Results.Ok(new { mensagem = "Perfil Criado!" });
});

app.MapPost("/login", (LoginDTO dadosLogin) => {
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PonteDB>();
    var user = db.Usuarios.FirstOrDefault(u => u.Email.ToLower() == dadosLogin.Email.Trim().ToLower());
    if (user != null && BCrypt.Net.BCrypt.Verify(dadosLogin.Senha, user.SenhaHash)) {
        return Results.Ok(new { id = user.ID, nome = user.Nome, imc = user.IMC, tmb = user.TMB, gasto = user.GastoTotal }); 
    }
    return Results.Json(new { mensagem = "E-mail ou senha incorretos." }, statusCode: 400);
});

// --- ROTA: ESQUECI MINHA SENHA (PASSO 1) ---
app.MapPost("/esqueci-senha", (EsqueceuSenhaDTO req) => {
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PonteDB>();
    var email = req.Email.Trim().ToLower();

    // 1. Verifica se o usuário existe
    var user = db.Usuarios.FirstOrDefault(u => u.Email.ToLower() == email);
    if (user == null)
        return Results.Json(new { mensagem = "E-mail não encontrado em nossa base." }, statusCode: 404);

    // 2. Gera o Token de 6 dígitos
    string token = new Random().Next(100000, 999999).ToString();

    // 3. CHAMA O SERVIÇO DE E-MAIL (Aqui ele envia para o Mailtrap)
    EmailService.EnviarToken(email, token);

    // 4. Retorna para o JS para que ele possa comparar o token depois
    return Results.Ok(new { mensagem = "Código enviado com sucesso!", tokenParaJs = token });
});

// --- ROTA: REDEFINIR SENHA (PASSO 2 - FINAL) ---
app.MapPost("/redefinir-senha", (RedefinicaoSenhaDTO req) => {
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PonteDB>();
    var email = req.Email.Trim().ToLower();

    // 1. Localiza o usuário
    var user = db.Usuarios.FirstOrDefault(u => u.Email.ToLower() == email);
    if (user == null)
        return Results.Json(new { mensagem = "Usuário não identificado." }, statusCode: 404);

    // 2. Criptografa a nova senha e salva
    user.SenhaHash = BCrypt.Net.BCrypt.HashPassword(req.NovaSenha);
    
    db.Usuarios.Update(user);
    db.SaveChanges();

    return Results.Ok(new { mensagem = "Senha atualizada com sucesso!" });
});
// Outras rotas permanecem...
 app.MapPost("/analisar-prato", async (
    [FromForm] IFormFile foto, 
    [FromForm] string porcao, 
    [FromForm] int usuarioId, 
    BSFM.Services.YoloInferenceService yolo, 
    BSFM.Services.UsdaNutritionService nutri, 
    PonteBanco.PonteDB db) => // Mantido conforme seu print
{
    // Validação de entrada: Evita erros se o usuário enviar sem foto
    if (foto == null || foto.Length == 0) 
        return Results.BadRequest(new { mensagem = "Nenhuma imagem foi recebida pelo servidor." });

    using var ms = new MemoryStream();
    await foto.CopyToAsync(ms);
    var imagemBytes = ms.ToArray();
    
    // 1. Chamar a IA (Esta função no CS deve limpar as aspas agora!)
    var alimentosPt = yolo.DetectarAlimentos(imagemBytes);

    Console.WriteLine($"[IA RESULT] Itens encontrados: {(alimentosPt.Any() ? string.Join(", ", alimentosPt) : "NADA")}");

    if (alimentosPt == null || alimentosPt.Count == 0) 
    {
        Console.WriteLine("[IA AVISO] Nenhum alimento detectado.");
        return Results.Json(new { mensagem = "A Inteligência Artificial não conseguiu ver comida. Tente focar melhor e aproximar do prato." }, statusCode: 404);
    }

    double caloriasTotal = 0, protTotal = 0, carbTotal = 0, gordTotal = 0;
    bool aoMenosUmSucesso = false;

    // 2. Loop para cada alimento detectado
    foreach (var nomePt in alimentosPt)
    {
        // 2.1 TRADUÇÃO REVERSA INTELIGENTE (Pega a CHAVE em inglês)
        // Adicionei um .Trim() para garantir que nenhuma sujeira entre no USDA
        string nomeEn = BSFM.Services.YoloInferenceService.Tradutor
                        .FirstOrDefault(x => x.Value.Equals(nomePt, StringComparison.OrdinalIgnoreCase)).Key 
                        ?? nomePt.Replace("'", "").Trim();

        // 2.2 Busca nutricional (Ex: "steak")
        var d = await nutri.BuscarNutrientes(nomeEn);
        
        if (d != null) 
        {
            // Note: Usei a sua escala de porções corrigida (muito melhor para frutas e pratos individuais)
            double mult = porcao.ToLower() switch { "pequeno" => 0.75, "medio" => 1.0, "grande" => 1.8, _ => 1.0 };
            caloriasTotal += (d.Calorias100g * mult);
            protTotal += (d.Proteinas100g * mult);
            carbTotal += (d.Carbos100g * mult);
            gordTotal += (d.Gorduras100g * mult);
            aoMenosUmSucesso = true;
        }
        else {
             Console.WriteLine($"[AVISO USDA] Sem dados para o alimento: {nomeEn}");
        }
    }

    // Se nenhum item foi achado no banco americano, avisamos o usuário
    if (!aoMenosUmSucesso)
        return Results.Json(new { mensagem = $"Não conseguimos dados nutricionais para: {string.Join(", ", alimentosPt)}" }, statusCode: 404);

    // 3. PERSISTÊNCIA NO POSTGRESQL (A grande vantagem do seu sistema)
    var analiseFinal = new ClassesBSFM.AnaliseIA {
        UsuarioID = usuarioId,
        Alimento = string.Join(", ", alimentosPt),
        Porcao = porcao,
        Calorias = Math.Round(caloriasTotal, 2),
        Proteinas = Math.Round(protTotal, 2),
        Carbos = Math.Round(carbTotal, 2),
        Gorduras = Math.Round(gordTotal, 2),
        DataAnalise = DateTime.Now
    };

    try {
        db.AnalisesIA.Add(analiseFinal);
        await db.SaveChangesAsync();
    } catch (Exception ex) {
        Console.WriteLine($"[ERRO BANCO] {ex.Message}");
    }

    return Results.Ok(new { 
        sucesso = true,
        dados = analiseFinal 
    });

}).DisableAntiforgery();

app.MapGet("/historico-analises/{usuarioId}", async (int usuarioId, PonteBanco.PonteDB db) => 
{
    var historico = await db.AnalisesIA
        .Where(a => a.UsuarioID == usuarioId)
        .OrderByDescending(a => a.DataAnalise)
        .ToListAsync();

    return Results.Ok(historico);
});

app.MapGet("/evolucao/{usuarioId}", async (int usuarioId, PonteBanco.PonteDB db) => {
    var logs = await db.Historicos
        .Where(h => h.UsuarioID == usuarioId)
        .OrderByDescending(h => h.DataRegistro)
        .ToListAsync();
    return Results.Ok(logs);
});

// ROTA: Registrar nova medição (Peso/Altura)
app.MapPost("/atualizar-medicao", async (HistoricoProgresso novaMedicao, PonteBanco.PonteDB db) => {
    // 1. Calcula o IMC para o histórico
    novaMedicao.IMC = Math.Round(novaMedicao.Peso / (novaMedicao.Altura * novaMedicao.Altura), 2);
    novaMedicao.DataRegistro = DateTime.Now;

    // 2. Salva no Histórico
    db.Historicos.Add(novaMedicao);

    // 3. Importante: Atualiza o peso/altura atual na tabela de Usuario também (para o dashboard mudar)
    var user = await db.Usuarios.FindAsync(novaMedicao.UsuarioID);
    if (user != null) {
        user.Peso = novaMedicao.Peso;
        user.Altura = novaMedicao.Altura;
        new CalcularNutricional().RegistrarCalculos(user); // Recalcula IMC/TMB/Gasto
        db.Usuarios.Update(user);
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { mensagem = "Medição registrada!", imc = novaMedicao.IMC, userAtualizado = user });
});

app.MapPost("/definir-meta", async (MetaDTO dados, PonteBanco.PonteDB db) => {
    var user = await db.Usuarios.FindAsync(dados.UsuarioId);
    if (user == null) return Results.NotFound();
    
    user.PesoMeta = dados.PesoMeta;
    await db.SaveChangesAsync();
    
    return Results.Ok(new { pesoMeta = user.PesoMeta });
});

app.Run(); // FINAL DO ARQUIVO

// Modelos de dados (DTOs)
public record LoginDTO(string Email, string Senha);
public record SolicitacaoEmail(string Email);
public record RedefinicaoSenha(string Email, string NovaSenha);
public record RedefinicaoFinal(string Email, string NovaSenha);
public record EsqueceuSenhaDTO(string Email);
public record RedefinicaoSenhaDTO(string Email, string NovaSenha);
public record MetaDTO(int UsuarioId, double PesoMeta);