using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using ClosedXML.Excel;

// ===================== CONFIG =====================
// FORÇAR SQLite - REMOVER variáveis de ambiente problemáticas ANTES de criar o builder
var envCs = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
if (!string.IsNullOrEmpty(envCs) && 
    (envCs.Contains("host=", StringComparison.OrdinalIgnoreCase) || 
     envCs.Contains("server=", StringComparison.OrdinalIgnoreCase) ||
     envCs.Contains("Port=", StringComparison.OrdinalIgnoreCase)))
{
    Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
    Console.WriteLine($"[WARNING] Variável de ambiente ConnectionStrings__Default removida (contém PostgreSQL/MySQL)");
}

var builder = WebApplication.CreateBuilder(args);

// FORÇAR SQLite - IGNORAR COMPLETAMENTE o sistema de configuração
// Usar apenas a connection string SQLite diretamente
var cs = "Data Source=appointments.db";

// Sobrescrever qualquer configuração que possa ter sido carregada
builder.Configuration["ConnectionStrings:Default"] = cs;

Console.WriteLine($"[INFO] FORÇANDO connection string SQLite: {cs}");
Console.WriteLine($"[INFO] Qualquer variável de ambiente será ignorada.");

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlite(cs));

// Configurar CORS com origens específicas
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() 
    ?? new[] { "http://localhost:3000", "http://localhost:5173", "http://localhost:8080" };

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.UseCors();

// ===================== HELPERS =====================
static string NormalizeCpf(string? cpf) => cpf is null ? "" : Regex.Replace(cpf, "[^0-9]", "");
static string NormalizePhone(string? p) => p is null ? "" : Regex.Replace(p, "[^0-9]", "");
static string ToLabel(string hhmm) { var p = hhmm.Split(':'); return $"{int.Parse(p[0])}H{(p[1]=="00"?"":p[1])}"; }
static bool IsAfternoonDay(DayOfWeek d) => d == DayOfWeek.Monday || d == DayOfWeek.Wednesday;
static bool IsMorningDay(DayOfWeek d) => d == DayOfWeek.Thursday;

// Verificar se quinta-feira está desabilitada a partir de 20/11/2025
static bool IsThursdayDisabled(DateOnly date)
{
    // Data limite: 20/11/2025
    var cutoffDate = new DateOnly(2025, 11, 20);
    return date.DayOfWeek == DayOfWeek.Thursday && date >= cutoffDate;
}

static bool IsAdmin(HttpRequest req, IConfiguration cfg)
{
    // PRIORIDADE: appsettings.json > padrão > ignorar variável de ambiente incorreta
    var envKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
    var configKey = cfg["ADMIN_KEY"];
    
    // Usar appsettings.json se disponível, senão usar padrão
    var cfgKey = configKey ?? "cac-coral-drpaulo";
    
    string? header = req.Headers["X-Admin-Key"];
    string? cookie = req.Cookies["admin_key"];
    string? query  = req.Query["key"];
    var supplied = header ?? cookie ?? query;
    return !string.IsNullOrWhiteSpace(supplied) && supplied == cfgKey;
}

// ===================== REGRAS DE HORÁRIOS =====================
string[] MorningSlots = new[]{
    "08:00","08:10","08:15","08:20","08:25","08:30","08:35","08:40","08:45","08:50","08:55",
    "09:00","09:10","09:15","09:20","09:25","09:30","09:35","09:40","09:45","09:50","09:55",
    "10:00","10:05","10:10","10:15","10:20","10:25","10:30","10:35","10:40","10:45","10:50","10:55",
    "11:00","11:05","11:10","11:15","11:25","11:30","11:35","11:40","11:45","11:50"
};

string[] AfternoonSlots = new[]{
    "13:00","13:05","13:10","13:15","13:20","13:25","13:30","13:35","13:40","13:45","13:50","13:55",
    "14:00","14:05","14:10","14:15","14:20","14:25","14:30","14:35","14:40","14:45","14:50","14:55",
    "15:00","15:05","15:10","15:15","15:20","15:25","15:30","15:35","15:40","15:45","15:50","15:55",
    "16:00","16:05","16:10","16:15","16:20","16:25","16:30","16:35","16:40","16:45","16:50"
};

string[] GetAllowedSlots(DateOnly d)
{
    // Se quinta-feira está desabilitada a partir de 20/11/2025, retornar vazio
    if (IsThursdayDisabled(d))
        return Array.Empty<string>();
    
    return IsAfternoonDay(d.DayOfWeek) ? AfternoonSlots :
           (IsMorningDay(d.DayOfWeek) ? MorningSlots : Array.Empty<string>());
}

// ===================== DB INIT =====================
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        // Tentar garantir que o banco está criado
        db.Database.EnsureCreated();
        
        // Criar tabelas manualmente se não existirem
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
        
        using var command = connection.CreateCommand();
        
        // Criar tabela BlockedDays
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS BlockedDays (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Date TEXT NOT NULL UNIQUE,
                Reason TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
        ";
        await command.ExecuteNonQueryAsync();
        
        // Criar índice único para BlockedDays
        command.CommandText = @"
            CREATE UNIQUE INDEX IF NOT EXISTS IX_BlockedDays_Date ON BlockedDays(Date);
        ";
        await command.ExecuteNonQueryAsync();
        
        // Criar tabela DaySchedules
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS DaySchedules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DayOfWeek INTEGER NOT NULL UNIQUE,
                TimeSlots TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
        ";
        await command.ExecuteNonQueryAsync();
        
        // Criar índice único para DaySchedules
        command.CommandText = @"
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DaySchedules_DayOfWeek ON DaySchedules(DayOfWeek);
        ";
        await command.ExecuteNonQueryAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Erro ao inicializar banco de dados");
    }
}

// ===================== PÚBLICO =====================
app.MapGet("/health", () => Results.Text("ok"));

// Slots — ?date=YYYY-MM-DD
app.MapGet("/api/slots", async (
    [FromQuery] string date,
    AppDbContext db,
    ILogger<Program> logger) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            logger.LogWarning("⚠️ Parâmetro 'date' vazio ou nulo");
            return Results.BadRequest(new { message = "Parâmetro 'date' é obrigatório. Use YYYY-MM-DD." });
        }

        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            logger.LogWarning("⚠️ Data inválida recebida: {Date}", date);
            return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });
        }

        // Verificar PRIMEIRO se quinta-feira está desabilitada (a partir de 20/11/2025)
        bool isThursdayDisabled = IsThursdayDisabled(d);
        logger.LogInformation("🔍 Verificação quinta-feira: Data={Date}, DayOfWeek={DayOfWeek}, IsDisabled={IsDisabled}", 
            d, d.DayOfWeek, isThursdayDisabled);
        
        if (isThursdayDisabled)
        {
            logger.LogWarning("🚫 QUINTA-FEIRA DESABILITADA: {Date} (dia {DayOfWeek}) - Retornando array vazio. Data limite: 20/11/2025", 
                d, d.DayOfWeek);
            return Results.Ok(new { date = d.ToString("yyyy-MM-dd"), slots = Array.Empty<object>() });
        }

        // ========== LÓGICA DIRETA E SIMPLES ==========
        string[] allowed;
        
        // SEMPRE BUSCAR DO BANCO (sem cache do EF Core)
        var customSchedule = await db.DaySchedules
            .Where(s => s.DayOfWeek == d.DayOfWeek)
            .FirstOrDefaultAsync();
        
        // Se encontrou customização E tem conteúdo válido
        if (customSchedule != null && 
            !string.IsNullOrWhiteSpace(customSchedule.TimeSlots) && 
            customSchedule.TimeSlots.Trim() != "[]" &&
            customSchedule.TimeSlots.Trim() != "null")
        {
            var customSlots = System.Text.Json.JsonSerializer.Deserialize<string[]>(customSchedule.TimeSlots);
            
            // USAR CUSTOMIZADOS se tiver pelo menos 1 slot
            if (customSlots != null && customSlots.Length > 0 && customSlots.Any(s => !string.IsNullOrWhiteSpace(s)))
            {
                allowed = customSlots.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                logger.LogInformation("✓ CUSTOMIZADOS: {DayOfWeek} ({Date}) = {Count} slots", 
                    d.DayOfWeek, d, allowed.Length);
            }
            else
            {
                allowed = GetAllowedSlots(d);
                logger.LogWarning("⚠ Custom vazio/null, usando PADRÃO: {DayOfWeek}", d.DayOfWeek);
            }
        }
        else
        {
            // USAR PADRÃO
            allowed = GetAllowedSlots(d);
            logger.LogInformation("→ PADRÃO: {DayOfWeek} ({Date}) = {Count} slots", 
                d.DayOfWeek, d, allowed.Length);
        }

        if (allowed.Length == 0)
            return Results.Ok(new { date = d.ToString("yyyy-MM-dd"), slots = Array.Empty<object>() });

        // Verificar se o dia está bloqueado (sem cache)
        bool isBlocked = await db.BlockedDays.AnyAsync(b => b.Date == d);
        
        if (isBlocked)
        {
            logger.LogInformation("🚫 BLOQUEADO: {Date} - Todos os slots serão marcados como bloqueados", d);
        }

        // Se o dia estiver bloqueado, retornar todos os slots como ocupados
        if (isBlocked)
        {
            var blockedSlots = allowed.Select(t => new {
                time = t,
                label = ToLabel(t),
                taken = true,
                blocked = true
            }).ToArray();
            logger.LogInformation("Retornando {Count} slots bloqueados para data {Date}.", blockedSlots.Length, d);
            return Results.Ok(new { date = d.ToString("yyyy-MM-dd"), slots = blockedSlots });
        }

        List<TimeOnly> takenTimes = new();
        try
        {
            takenTimes = await db.Appointments
                .Where(a => a.Date == d)
                .Select(a => a.Time)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao consultar horários ocupados para {Date}. Devolvendo todos como livres.", d);
            takenTimes = new List<TimeOnly>();
        }

        var slots = allowed.Select(t => new {
            time = t,
            label = ToLabel(t),
            taken = takenTimes.Contains(TimeOnly.ParseExact(t, "HH:mm", CultureInfo.InvariantCulture)),
            blocked = false
        }).ToArray();

        logger.LogInformation("✅ Retornando {Count} slots para data {Date}", slots.Length, d);
        return Results.Ok(new { date = d.ToString("yyyy-MM-dd"), slots });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "❌ Erro ao processar requisição /api/slots para data {Date}", date);
        return Results.Problem(
            detail: $"Erro ao processar requisição: {ex.Message}",
            statusCode: 500
        );
    }
});

// ===================== Criar agendamento =====================
app.MapPost("/api/appointments", async ([FromBody] AppointmentDto input, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(input.FullName) || string.IsNullOrWhiteSpace(input.CPF))
        return Results.BadRequest(new { message = "Nome e CPF são obrigatórios." });

    if (!DateOnly.TryParseExact(input.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });

    // Verificar se quinta-feira está desabilitada (a partir de 20/11/2025)
    if (IsThursdayDisabled(d))
        return Results.BadRequest(new { message = "Não atendemos às quintas-feiras a partir de 20/11/2025. Por favor, selecione segunda ou quarta-feira." });

    if (!TimeOnly.TryParseExact(input.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        return Results.BadRequest(new { message = "Horário inválido. Use HH:mm." });

    var cat = (input.Category ?? "").Trim().ToUpperInvariant();

    // Categorias (inclui AB/AC/AD/AE)
    var valid = new[] { "A","B","C","D","E","AB","AC","AD","AE" };
    if (!valid.Contains(cat))
        return Results.BadRequest(new { message = "Categoria inválida. Use A, B, C, D, E, AB, AC, AD ou AE." });

    // Verificar se o dia está bloqueado
    var isBlocked = await db.BlockedDays.AnyAsync(b => b.Date == d);
    if (isBlocked)
        return Results.BadRequest(new { message = "Este dia está bloqueado para agendamentos." });

    // Regras por dia - MESMA lógica do /slots
    string[] allowedToday;
    db.ChangeTracker.Clear();
    var customSchedule = await db.DaySchedules
        .Where(s => s.DayOfWeek == d.DayOfWeek)
        .FirstOrDefaultAsync();
        
    if (customSchedule != null && 
        !string.IsNullOrWhiteSpace(customSchedule.TimeSlots) &&
        customSchedule.TimeSlots.Trim() != "[]" &&
        customSchedule.TimeSlots.Trim() != "null")
    {
        var customSlots = System.Text.Json.JsonSerializer.Deserialize<string[]>(customSchedule.TimeSlots);
        if (customSlots != null && customSlots.Length > 0 && customSlots.Any(s => !string.IsNullOrWhiteSpace(s)))
        {
            allowedToday = customSlots.Where(s => !string.IsNullOrWhiteSpace(s)).Select(x => x.Trim()).ToArray();
        }
        else
        {
            allowedToday = GetAllowedSlots(d).Select(x => x.Trim()).ToArray();
        }
    }
    else
    {
        allowedToday = GetAllowedSlots(d).Select(x => x.Trim()).ToArray();
    }
    
    if (!allowedToday.Contains(input.Time))
        return Results.BadRequest(new { message = "Este horário não é permitido para este dia." });

    // Psicotécnico OPCIONAL; Toxicológico exigido para as categorias abaixo
    var needsTox = new HashSet<string>{ "C","D","E","AC","AD","AE" };
    if (needsTox.Contains(cat))
    {
        if (!input.Foto || !input.Toxicologico)
            return Results.BadRequest(new { message = "Para C, D, E, AC, AD e AE é obrigatório confirmar Foto e Toxicológico (validade 3 meses)." });
    }
    else
    {
        if (!input.Foto)
            return Results.BadRequest(new { message = "Para A, B e AB é obrigatório confirmar Foto." });
    }

    // Bloqueio por CPF OU Telefone (duplicidade)
    var cpfNorm   = NormalizeCpf(input.CPF);
    var phoneNorm = NormalizePhone(input.Phone);

    var existsCpf = await db.Appointments.AsNoTracking().AnyAsync(a => a.CPF == cpfNorm);

    var existsPhone = false;
    if (!string.IsNullOrEmpty(phoneNorm))
    {
        existsPhone = await db.Appointments.AsNoTracking().AnyAsync(a => a.Phone == phoneNorm);
        if (!existsPhone)
        {
            var phones = await db.Appointments.AsNoTracking().Select(a => a.Phone).ToListAsync();
            existsPhone = phones.Any(p => NormalizePhone(p) == phoneNorm);
        }
    }

    if (existsCpf || existsPhone)
        return Results.Conflict(new { message = "Já existe um agendamento para este CPF ou Telefone. Utilize Reagendar ou Cancelar." });

    var ap = new Appointment
    {
        FullName = input.FullName.Trim(),
        CPF = cpfNorm,
        Phone = phoneNorm,
        Date = d,
        Time = t,
        Taxa = input.Taxa ?? "",
        Category = cat,
        Foto = input.Foto,
        Psicotecnico = input.Psicotecnico, // opcional
        Toxicologico = input.Toxicologico,
        CreatedAt = DateTime.UtcNow
    };

    try
    {
        db.Appointments.Add(ap);
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE") == true)
    {
        return Results.Conflict(new { message = "Este horário acabou de ser reservado. Escolha outro horário." });
    }

    return Results.Ok(new { message = "Agendamento criado com sucesso.", id = ap.Id });
});

// ===================== Listar agendamentos =====================
app.MapGet("/api/appointments", async ([FromQuery] string? date, AppDbContext db) =>
{
    IQueryable<Appointment> q = db.Appointments.AsNoTracking();

    if (!string.IsNullOrWhiteSpace(date)
        && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
    {
        q = q.Where(a => a.Date == d);
    }

    var list = await q.OrderBy(a => a.Date).ThenBy(a => a.Time).ToListAsync();

    return Results.Ok(list);
});

// ===================== Comprovante (HTML) =====================
app.MapGet("/api/appointments/{id:int}/confirmation", async (int id, AppDbContext db) =>
{
    var ap = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);

    if (ap is null) return Results.NotFound(new { message = "Agendamento não encontrado." });

    var sb = new StringBuilder();

    sb.Append(@"<!doctype html><html lang='pt-BR'><head>");
    sb.Append(@"<meta charset='utf-8'><title>Confirmação de Agendamento</title><style>");
    sb.Append(@"body{font-family:Arial,Helvetica,sans-serif;margin:24px}");
    sb.Append(@"h1{font-size:20px;margin:0 0 10px}");
    sb.Append(@"table{width:100%;border-collapse:collapse}");
    sb.Append(@"th,td{text-align:left;padding:8px;border-bottom:1px solid #eee}");
    sb.Append(@".muted{color:#6b7280}");
    sb.Append(@"</style></head><body>");

    sb.Append("<h1>Confirmação de Agendamento</h1><table>");
    sb.Append($"<tr><th>ID</th><td>{ap.Id}</td></tr>");
    sb.Append($"<tr><th>Nome</th><td>{ap.FullName}</td></tr>");
    sb.Append($"<tr><th>CPF</th><td>{ap.CPF}</td></tr>");
    sb.Append($"<tr><th>Telefone</th><td>{ap.Phone}</td></tr>");
    sb.Append($"<tr><th>Categoria</th><td>{ap.Category}</td></tr>");
    sb.Append($"<tr><th>Taxa</th><td>{ap.Taxa}</td></tr>");
    sb.Append($"<tr><th>Data</th><td>{ap.Date:yyyy-MM-dd}</td></tr>");
    sb.Append($"<tr><th>Hora</th><td>{ap.Time:HH\\:mm}</td></tr>");
    sb.Append($"<tr><th>Foto</th><td>{(ap.Foto ? "Sim" : "Não")}</td></tr>");
    sb.Append($"<tr><th>Psicotécnico</th><td>{(ap.Psicotecnico ? "Sim" : "Não")}</td></tr>");
    sb.Append($"<tr><th>Toxicológico</th><td>{(ap.Toxicologico ? "Sim" : "Não")} <span class='muted'>(validade 3 meses)</span></td></tr>");
    sb.Append($"<tr><th>Criado em</th><td>{ap.CreatedAt:yyyy-MM-dd HH\\:mm} (UTC)</td></tr>");
    sb.Append("</table></body></html>");

    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
    return Results.File(bytes, "text/html; charset=utf-8", $"confirmacao-{ap.Id}.html");
});

// ===================== Download do questionário (PDF) =====================
app.MapGet("/api/questionario/download", (IWebHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "Assets", "questionario.pdf");
    if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "Assets", "questionario.pdf");
    if (!File.Exists(path)) return Results.NotFound(new { message = "Arquivo não encontrado em 'Assets/questionario.pdf'." });
    var bytes = File.ReadAllBytes(path);
    return Results.File(bytes, "application/pdf", "questionario.pdf");
});

// ===================== Reagendar (CPF + Telefone) =====================
app.MapPost("/api/appointments/by-cpf/reschedule", async ([FromBody] RescheduleDto input, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(input.Cpf) || string.IsNullOrWhiteSpace(input.Phone) ||
        string.IsNullOrWhiteSpace(input.Date) || string.IsNullOrWhiteSpace(input.Time))
        return Results.BadRequest(new { message = "Informe CPF, Telefone, nova data e novo horário." });

    if (!DateOnly.TryParseExact(input.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var newDate))
        return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });
    
    // Verificar se quinta-feira está desabilitada (a partir de 20/11/2025)
    if (IsThursdayDisabled(newDate))
        return Results.BadRequest(new { message = "Não atendemos às quintas-feiras a partir de 20/11/2025. Por favor, selecione segunda ou quarta-feira." });
    
    if (!TimeOnly.TryParseExact(input.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var newTime))
        return Results.BadRequest(new { message = "Horário inválido. Use HH:mm." });

    var cpf = NormalizeCpf(input.Cpf);
    var phone = NormalizePhone(input.Phone);

    var allCpf = await db.Appointments
        .Where(a => a.CPF == cpf)
        .OrderBy(a => a.Date).ThenBy(a => a.Time)
        .ToListAsync();

    var all = allCpf.Where(a => NormalizePhone(a.Phone) == phone).ToList();
    if (all.Count == 0) return Results.NotFound(new { message = "Nenhum agendamento encontrado para este CPF/Telefone." });

    var today = DateOnly.FromDateTime(DateTime.Now);
    var nowT  = TimeOnly.FromDateTime(DateTime.Now);

    var ap = all.FirstOrDefault(a => a.Date > today || (a.Date == today && a.Time > nowT)) ?? all.Last();

    // Verificar horários customizados ou usar padrão - MESMA lógica do /slots
    string[] allowed;
    db.ChangeTracker.Clear(); // Garantir leitura fresca
    var customSchedule = await db.DaySchedules
        .Where(s => s.DayOfWeek == newDate.DayOfWeek)
        .FirstOrDefaultAsync();
        
    if (customSchedule != null && 
        !string.IsNullOrWhiteSpace(customSchedule.TimeSlots) &&
        customSchedule.TimeSlots.Trim() != "[]" &&
        customSchedule.TimeSlots.Trim() != "null")
    {
        var customSlots = System.Text.Json.JsonSerializer.Deserialize<string[]>(customSchedule.TimeSlots);
        if (customSlots != null && customSlots.Length > 0 && customSlots.Any(s => !string.IsNullOrWhiteSpace(s)))
        {
            allowed = customSlots.Where(s => !string.IsNullOrWhiteSpace(s)).Select(x => x.Trim()).ToArray();
        }
        else
        {
            allowed = GetAllowedSlots(newDate).Select(x => x.Trim()).ToArray();
        }
    }
    else
    {
        allowed = GetAllowedSlots(newDate).Select(x => x.Trim()).ToArray();
    }
    
    if (!allowed.Contains(input.Time))
        return Results.BadRequest(new { message = "Este horário não é permitido para este dia." });

    var conflict = await db.Appointments.AnyAsync(a => a.Id != ap.Id && a.Date == newDate && a.Time == newTime);
    if (conflict) return Results.Conflict(new { message = "Este horário já está reservado." });

    ap.Date = newDate;
    ap.Time = newTime;

    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE") == true) {
        return Results.Conflict(new { message = "Conflito de horário. Tente outro." });
    }

    return Results.Ok(new { message = "Reagendamento realizado com sucesso.", id = ap.Id });
});

// ===================== Cancelar (CPF + Telefone) =====================
app.MapPost("/api/appointments/by-cpf/cancel", async ([FromBody] CancelDto input, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(input.Cpf) || string.IsNullOrWhiteSpace(input.Phone))
        return Results.BadRequest(new { message = "Informe o CPF e o Telefone para cancelar." });

    var cpf = NormalizeCpf(input.Cpf);
    var phone = NormalizePhone(input.Phone);

    var allCpf = await db.Appointments
        .Where(a => a.CPF == cpf)
        .OrderBy(a => a.Date).ThenBy(a => a.Time)
        .ToListAsync();

    var all = allCpf.Where(a => NormalizePhone(a.Phone) == phone).ToList();
    if (all.Count == 0) return Results.NotFound(new { message = "Nenhum agendamento encontrado para este CPF/Telefone." });

    var today = DateOnly.FromDateTime(DateTime.Now);
    var nowT  = TimeOnly.FromDateTime(DateTime.Now);

    var ap = all.FirstOrDefault(a => a.Date > today || (a.Date == today && a.Time > nowT)) ?? all.Last();

    db.Appointments.Remove(ap);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Agendamento cancelado e horário liberado.", id = ap.Id });
});

// ===================== ADMIN-ONLY =====================

// 2) Lista do dia
app.MapGet("/api/admin/appointments", async (HttpContext ctx, [FromQuery] string date, AppDbContext db, IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();
    if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });

    var appointments = await db.Appointments.AsNoTracking()
        .Where(a => a.Date == d)
        .OrderBy(a => a.Time)
        .ToListAsync();

    if (!appointments.Any())
        return Results.NotFound(new { message = "Sem agendamentos para essa data." });

    var items = appointments.Select(a => new {
        time     = a.Time.ToString("HH:mm", CultureInfo.InvariantCulture),
        label    = ToLabel(a.Time.ToString("HH:mm", CultureInfo.InvariantCulture)),
        name     = a.FullName,
        cpf      = a.CPF,
        phone    = a.Phone,
        category = a.Category,
        taxa     = a.Taxa,
        foto     = a.Foto,
        psi      = a.Psicotecnico,
        tox      = a.Toxicologico,
        id       = a.Id
    }).ToList();

    return Results.Ok(new { date = d, count = items.Count, items });
});

// 2.6) PATCH (Nome/CPF/Telefone)
app.MapMethods("/api/admin/appointments/{id:int}", new[] { "PATCH" }, async (
    HttpContext ctx,
    int id,
    [FromBody] AdminEditDto input,
    AppDbContext db,
    IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();

    var ap = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id);
    if (ap is null) return Results.NotFound(new { message = "Agendamento não encontrado." });

    var fullName = (input.FullName ?? "").Trim();
    var cpf      = NormalizeCpf(input.Cpf);
    var phone    = NormalizePhone(input.Phone);

    if (string.IsNullOrWhiteSpace(fullName)) return Results.BadRequest(new { message = "Informe o Nome." });
    if (cpf.Length   < 11)                  return Results.BadRequest(new { message = "CPF inválido." });
    if (phone.Length < 8)                   return Results.BadRequest(new { message = "Telefone inválido." });

    ap.FullName = fullName;
    ap.CPF      = cpf;
    ap.Phone    = phone;

    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Atualizado." });
});

// 3) Relatório diário XLSX
app.MapGet("/api/reports/daily", async (HttpContext ctx, [FromQuery] string date, AppDbContext db, IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();

    if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });

    var list = await db.Appointments.AsNoTracking()
        .Where(a => a.Date == d)
        .OrderBy(a => a.Time)
        .ToListAsync();

    using var wb = new XLWorkbook();

    var ws = wb.Worksheets.Add("Atendimentos");
    var headers = new[] { "ID","Nome","CPF","Telefone","Categoria","Taxa","Data","Hora","Foto","Psicotécnico","Toxicológico" };
    for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
    ws.Row(1).Style.Font.Bold = true;

    int r = 2;
    foreach (var ap in list)
    {
        ws.Cell(r, 1).Value = ap.Id;
        ws.Cell(r, 2).Value = ap.FullName;
        ws.Cell(r, 3).Value = ap.CPF;
        ws.Cell(r, 4).Value = ap.Phone;
        ws.Cell(r, 5).Value = ap.Category;
        ws.Cell(r, 6).Value = ap.Taxa;
        ws.Cell(r, 7).Value = ap.Date.ToString("yyyy-MM-dd");
        ws.Cell(r, 8).Value = ap.Time.ToString("HH\\:mm");
        ws.Cell(r, 9).Value = ap.Foto ? "Sim" : "Não";
        ws.Cell(r,10).Value = ap.Psicotecnico ? "Sim" : "Não";
        ws.Cell(r,11).Value = ap.Toxicologico ? "Sim" : "Não";
        r++;
    }
    ws.Columns().AdjustToContents();

    var sum = wb.Worksheets.Add("Resumo");
    sum.Cell("A1").Value = "Relatório diário";
    sum.Cell("B1").Value = d.ToString("yyyy-MM-dd");
    sum.Cell("A3").Value = "Total de atendimentos";
    sum.Cell("B3").Value = list.Count;
    sum.Row(1).Style.Font.Bold = true;
    sum.Row(3).Style.Font.Bold = true;

    sum.Cell("A5").Value = "Categoria";
    sum.Cell("B5").Value = "Qtde";
    sum.Row(5).Style.Font.Bold = true;
    int rr = 6;
    foreach (var g in list.GroupBy(a => a.Category).OrderBy(g => g.Key))
    {
        sum.Cell(rr, 1).Value = g.Key;
        sum.Cell(rr, 2).Value = g.Count();
        rr++;
    }
    sum.Columns().AdjustToContents();

    using var ms = new MemoryStream();
    wb.SaveAs(ms);
    var bytes = ms.ToArray();
    var fileName = $"relatorio-{d:yyyy-MM-dd}.xlsx";
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
});

app.MapGet("/api/backup", async (AppDbContext db) =>
{
    var appointments = await db.Appointments.AsNoTracking().ToListAsync();
    var csvBuilder = new StringBuilder();
    csvBuilder.AppendLine("Id,FullName,CPF,Phone,Date,Time,Taxa,Category,Foto,Psicotecnico,Toxicologico,CreatedAt");

    foreach (var ap in appointments)
    {
        csvBuilder.AppendLine($"{ap.Id},{ap.FullName},{ap.CPF},{ap.Phone},{ap.Date},{ap.Time},{ap.Taxa},{ap.Category},{ap.Foto},{ap.Psicotecnico},{ap.Toxicologico},{ap.CreatedAt}");
    }

    var backupFileName = $"backup-{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}.csv";
    var backupFolderPath = Path.Combine(AppContext.BaseDirectory, "backups");
    Directory.CreateDirectory(backupFolderPath);
    var backupFilePath = Path.Combine(backupFolderPath, backupFileName);
    await File.WriteAllTextAsync(backupFilePath, csvBuilder.ToString());
    
    return Results.File(backupFilePath, "text/csv", backupFileName);
});

// ==================== Bloquear Dias ====================
app.MapPost("/api/admin/block-day", async (HttpContext ctx, [FromBody] BlockDayDto input, AppDbContext db, IConfiguration cfg, ILogger<Program> logger) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();
    
    // Converter DateTime para DateOnly, garantindo usar a data local
    DateOnly d;
    if (input.Date.Kind == DateTimeKind.Utc)
    {
        d = DateOnly.FromDateTime(input.Date.ToLocalTime().Date);
    }
    else
    {
        d = DateOnly.FromDateTime(input.Date.Date);
    }
    
    // Validação adicional
    if (d == default)
    {
        return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });
    }

    try
    {
        // Verificar se já existe bloqueio
        var existing = await db.BlockedDays.FirstOrDefaultAsync(b => b.Date == d);
        if (existing != null)
        {
            return Results.Ok(new { message = "Data já está bloqueada." });
        }

        // Criar bloqueio
        var blockedDay = new BlockedDay
        {
            Date = d,
            Reason = input.Reason ?? "Bloqueado pelo administrador",
            CreatedAt = DateTime.UtcNow
        };
        db.BlockedDays.Add(blockedDay);
        await db.SaveChangesAsync();
        
        // Log para debug
        logger.LogInformation("Data {Date} bloqueada com sucesso. Data salva: {SavedDate}", d, blockedDay.Date);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }

    return Results.Ok(new { message = "Data bloqueada com sucesso. Todos os horários aparecerão como ocupados." });
});

app.MapPost("/api/admin/unblock-day", async (HttpContext ctx, [FromBody] BlockDayDto input, AppDbContext db, IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();
    
    // Converter DateTime para DateOnly, garantindo usar a data local
    DateOnly d;
    if (input.Date.Kind == DateTimeKind.Utc)
    {
        d = DateOnly.FromDateTime(input.Date.ToLocalTime().Date);
    }
    else
    {
        d = DateOnly.FromDateTime(input.Date.Date);
    }
    
    // Validação adicional
    if (d == default)
    {
        return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });
    }

    try
    {
        var blockedDay = await db.BlockedDays.FirstOrDefaultAsync(b => b.Date == d);
        if (blockedDay == null)
        {
            return Results.NotFound(new { message = "Data não está bloqueada." });
        }

        db.BlockedDays.Remove(blockedDay);
        await db.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }

    return Results.Ok(new { message = "Data desbloqueada com sucesso." });
});

// ==================== Listar Dias Bloqueados ====================
app.MapGet("/api/admin/blocked-days", async (HttpContext ctx, AppDbContext db, IConfiguration cfg, ILogger<Program> logger) =>
{
    logger.LogInformation("Requisição recebida em /api/admin/blocked-days. Query key: {QueryKey}, Header key: {HeaderKey}", 
        ctx.Request.Query["key"].ToString(), ctx.Request.Headers["X-Admin-Key"].ToString());
    
    if (!IsAdmin(ctx.Request, cfg))
    {
        var cfgKey = cfg["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY") ?? "troque-esta-chave";
        logger.LogWarning("Tentativa de acesso não autorizado ao endpoint /api/admin/blocked-days. Key esperada: {ExpectedKey}, Key recebida: {ReceivedKey}", 
            cfgKey,
            ctx.Request.Query["key"].ToString() ?? ctx.Request.Headers["X-Admin-Key"].ToString() ?? "nenhuma");
        return Results.Json(new { error = "Unauthorized", message = "Acesso negado" }, statusCode: 401);
    }
    
    try
    {
        logger.LogInformation("Consultando dias bloqueados no banco de dados");
        var blockedDays = await db.BlockedDays
            .OrderByDescending(b => b.Date)
            .Select(b => new { 
                id = b.Id, 
                date = b.Date.ToString("yyyy-MM-dd"), 
                reason = b.Reason,
                createdAt = b.CreatedAt 
            })
            .ToListAsync();
        
        logger.LogInformation("Total de dias bloqueados encontrados: {Count}", blockedDays.Count);
        
        if (blockedDays.Any())
        {
            logger.LogInformation("Dias bloqueados: {Dates}", string.Join(", ", blockedDays.Select(b => b.date)));
        }
        
        var result = new { blockedDays };
        logger.LogInformation("Retornando resultado com {Count} dias bloqueados", blockedDays.Count);
        
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Erro ao listar dias bloqueados");
        return Results.Problem(ex.Message);
    }
});

// ==================== Gerenciar Horários por Dia da Semana ====================
app.MapGet("/api/admin/day-schedules", async (HttpContext ctx, AppDbContext db, IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();
    
    var schedules = await db.DaySchedules.ToListAsync();
    var result = schedules.Select(s => new {
        dayOfWeek = (int)s.DayOfWeek,
        dayName = s.DayOfWeek.ToString(),
        timeSlots = string.IsNullOrWhiteSpace(s.TimeSlots) 
            ? Array.Empty<string>() 
            : System.Text.Json.JsonSerializer.Deserialize<string[]>(s.TimeSlots) ?? Array.Empty<string>(),
        updatedAt = s.UpdatedAt
    }).ToList();

    return Results.Ok(new { schedules = result });
});

app.MapGet("/api/admin/day-schedule/{dayOfWeek:int}", async (HttpContext ctx, int dayOfWeek, AppDbContext db, IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();
    if (dayOfWeek < 0 || dayOfWeek > 6) return Results.BadRequest(new { message = "Dia da semana inválido (0-6)." });

    var schedule = await db.DaySchedules.FirstOrDefaultAsync(s => s.DayOfWeek == (DayOfWeek)dayOfWeek);
    if (schedule == null || string.IsNullOrWhiteSpace(schedule.TimeSlots))
    {
        // Retornar horários padrão
        var testDate = DateOnly.FromDateTime(DateTime.Now.AddDays((dayOfWeek - (int)DateTime.Now.DayOfWeek + 7) % 7));
        var defaultSlots = GetAllowedSlots(testDate);
        return Results.Ok(new {
            dayOfWeek = dayOfWeek,
            dayName = ((DayOfWeek)dayOfWeek).ToString(),
            timeSlots = defaultSlots,
            isCustom = false
        });
    }

    // Verificar se a lista customizada não está vazia
    var customSlots = System.Text.Json.JsonSerializer.Deserialize<string[]>(schedule.TimeSlots);
    if (customSlots == null || customSlots.Length == 0)
    {
        // Se estiver vazia, retornar padrão
        var testDate = DateOnly.FromDateTime(DateTime.Now.AddDays((dayOfWeek - (int)DateTime.Now.DayOfWeek + 7) % 7));
        var defaultSlots = GetAllowedSlots(testDate);
        return Results.Ok(new {
            dayOfWeek = dayOfWeek,
            dayName = ((DayOfWeek)dayOfWeek).ToString(),
            timeSlots = defaultSlots,
            isCustom = false
        });
    }

    return Results.Ok(new {
        dayOfWeek = dayOfWeek,
        dayName = schedule.DayOfWeek.ToString(),
        timeSlots = customSlots,
        isCustom = true,
        updatedAt = schedule.UpdatedAt
    });
});

app.MapPost("/api/admin/day-schedule", async (HttpContext ctx, [FromBody] DayScheduleDto input, AppDbContext db, IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();
    if (input.DayOfWeek < 0 || input.DayOfWeek > 6)
        return Results.BadRequest(new { message = "Dia da semana inválido (0-6)." });

    try
    {
        // Validar formato dos horários (deve ser HH:mm)
        if (input.TimeSlots != null)
        {
            foreach (var slot in input.TimeSlots)
            {
                if (!TimeOnly.TryParseExact(slot, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    return Results.BadRequest(new { message = $"Formato de horário inválido: '{slot}'. Use o formato HH:mm (ex: 08:30)." });
                }
            }
        }
        
        var schedule = await db.DaySchedules.FirstOrDefaultAsync(s => s.DayOfWeek == (DayOfWeek)input.DayOfWeek);
        var timeSlotsJson = System.Text.Json.JsonSerializer.Serialize(input.TimeSlots ?? Array.Empty<string>());
        
        var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Salvando horários customizados para {DayOfWeek}: {Count} slots. JSON: {Json}", 
            (DayOfWeek)input.DayOfWeek, input.TimeSlots?.Length ?? 0, timeSlotsJson);

        if (schedule == null)
        {
            schedule = new DaySchedule
            {
                DayOfWeek = (DayOfWeek)input.DayOfWeek,
                TimeSlots = timeSlotsJson,
                UpdatedAt = DateTime.UtcNow
            };
            db.DaySchedules.Add(schedule);
        }
        else
        {
            schedule.TimeSlots = timeSlotsJson;
            schedule.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        
        // FORÇAR FLUSH: Desanexar todas as entidades e forçar nova leitura
        db.ChangeTracker.Clear();
        
        // LER DIRETO DO BANCO (sem cache)
        var savedSchedule = await db.DaySchedules
            .Where(s => s.DayOfWeek == (DayOfWeek)input.DayOfWeek)
            .FirstOrDefaultAsync();
            
        if (savedSchedule != null)
        {
            var savedSlots = System.Text.Json.JsonSerializer.Deserialize<string[]>(savedSchedule.TimeSlots) ?? Array.Empty<string>();
            logger.LogInformation("✓✓✓ SALVO E VERIFICADO: {DayOfWeek} = {Count} slots", 
                (DayOfWeek)input.DayOfWeek, savedSlots.Length);
                
            return Results.Ok(new { 
                message = $"Horários atualizados para {((DayOfWeek)input.DayOfWeek).ToString()}.",
                dayOfWeek = (int)(DayOfWeek)input.DayOfWeek,
                dayName = ((DayOfWeek)input.DayOfWeek).ToString(),
                slotsCount = savedSlots.Length,
                slots = savedSlots,
                saved = true,
                verified = true
            });
        }
        else
        {
            logger.LogError("✗✗✗ ERRO: Não foi possível verificar salvamento para {DayOfWeek}", (DayOfWeek)input.DayOfWeek);
            return Results.Problem("Horários podem não ter sido salvos corretamente.");
        }
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapDelete("/api/admin/day-schedule/{dayOfWeek:int}", async (HttpContext ctx, int dayOfWeek, AppDbContext db, IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();
    if (dayOfWeek < 0 || dayOfWeek > 6)
        return Results.BadRequest(new { message = "Dia da semana inválido (0-6)." });

    try
    {
        var schedule = await db.DaySchedules.FirstOrDefaultAsync(s => s.DayOfWeek == (DayOfWeek)dayOfWeek);
        if (schedule == null)
        {
            return Results.NotFound(new { message = "Horários customizados não encontrados para este dia." });
        }

        db.DaySchedules.Remove(schedule);
        await db.SaveChangesAsync();
        
        // Verificar se foi removido
        var verify = await db.DaySchedules.FirstOrDefaultAsync(s => s.DayOfWeek == (DayOfWeek)dayOfWeek);
        if (verify == null)
        {
            return Results.Ok(new { 
                message = $"Horários resetados para padrão em {((DayOfWeek)dayOfWeek).ToString()}.",
                reset = true
            });
        }
        else
        {
            return Results.Problem("Falha ao remover horários customizados.");
        }
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ==================== Endpoint de Validação e Sincronização ====================
app.MapGet("/api/admin/verify-schedule/{dayOfWeek:int}", async (HttpContext ctx, int dayOfWeek, AppDbContext db, IConfiguration cfg, ILogger<Program> logger) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.Unauthorized();
    if (dayOfWeek < 0 || dayOfWeek > 6)
        return Results.BadRequest(new { message = "Dia da semana inválido (0-6)." });

    try
    {
        var schedule = await db.DaySchedules
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.DayOfWeek == (DayOfWeek)dayOfWeek);
            
        if (schedule == null || string.IsNullOrWhiteSpace(schedule.TimeSlots))
        {
            var testDate = DateOnly.FromDateTime(DateTime.Now.AddDays((dayOfWeek - (int)DateTime.Now.DayOfWeek + 7) % 7));
            var defaultSlots = GetAllowedSlots(testDate);
            
            return Results.Ok(new {
                dayOfWeek = dayOfWeek,
                dayName = ((DayOfWeek)dayOfWeek).ToString(),
                hasCustomSchedule = false,
                isCustom = false,
                timeSlots = defaultSlots,
                slotsCount = defaultSlots.Length,
                message = "Usando horários padrão"
            });
        }

        var customSlots = System.Text.Json.JsonSerializer.Deserialize<string[]>(schedule.TimeSlots);
        var isValid = customSlots != null && customSlots.Length > 0;
        
        logger.LogInformation("VERIFICAÇÃO: {DayOfWeek} - Customizado: {IsCustom}, Slots: {Count}", 
            (DayOfWeek)dayOfWeek, isValid, customSlots?.Length ?? 0);

        return Results.Ok(new {
            dayOfWeek = dayOfWeek,
            dayName = schedule.DayOfWeek.ToString(),
            hasCustomSchedule = true,
            isCustom = isValid,
            timeSlots = isValid ? customSlots : Array.Empty<string>(),
            slotsCount = customSlots?.Length ?? 0,
            updatedAt = schedule.UpdatedAt,
            json = schedule.TimeSlots,
            message = isValid ? "Horários customizados ativos" : "Horários customizados vazios, usando padrão"
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Erro ao verificar schedule para {DayOfWeek}", dayOfWeek);
        return Results.Problem(ex.Message);
    }
});

// ==================== Adicionar Novo Horário ====================
app.MapPost("/api/admin/add-time", async (HttpContext ctx, [FromBody] NewTimeDto input, AppDbContext db, IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();

    if (!DateOnly.TryParseExact(input.Date.ToString("yyyy-MM-dd"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });

    if (!TimeOnly.TryParseExact(input.Time.ToString("HH:mm"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        return Results.BadRequest(new { message = "Horário inválido. Use HH:mm." });

    var newSlot = new Appointment
    {
        FullName = input.FullName,
        CPF = input.CPF,
        Phone = input.Phone,
        Date = d,
        Time = t,
        Taxa = input.Taxa ?? "",
        Category = input.Category ?? "",
        Foto = input.Foto,
        Psicotecnico = input.Psicotecnico, // opcional
        Toxicologico = input.Toxicologico,
        CreatedAt = DateTime.UtcNow
    };

    try
    {
        await db.Appointments.AddAsync(newSlot);
        await db.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }

    return Results.Ok(new { message = "Novo horário adicionado com sucesso!" });
});

// ==================== Importar Agendamentos (CSV/XLSX) ====================
app.MapPost("/api/admin/import", async (HttpContext ctx, AppDbContext db, IConfiguration cfg, ILogger<Program> logger) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.Unauthorized();

    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.FirstOrDefault(f => f.Name == "file");
    
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { message = "Nenhum arquivo enviado." });

    var errors = new List<string>();
    var imported = 0;
    var skipped = 0;

    try
    {
        using var stream = file.OpenReadStream();
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        if (extension == ".csv")
        {
            // Processar CSV
            using var reader = new StreamReader(stream);
            var headerLine = await reader.ReadLineAsync();
            if (headerLine == null)
            {
                return Results.BadRequest(new { message = "Arquivo CSV vazio ou inválido." });
            }

            // Pular header e processar linhas
            string? line;
            int lineNum = 1;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                lineNum++;
                try
                {
                    var parts = line.Split(',');
                    if (parts.Length < 11) 
                    {
                        errors.Add($"Linha {lineNum}: Campos insuficientes");
                        skipped++;
                        continue;
                    }

                    // Id,FullName,CPF,Phone,Date,Time,Taxa,Category,Foto,Psicotecnico,Toxicologico,CreatedAt
                    var fullName = parts[1]?.Trim() ?? "";
                    var cpf = NormalizeCpf(parts[2]);
                    var phone = NormalizePhone(parts[3]);
                    if (!DateOnly.TryParseExact(parts[4]?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        errors.Add($"Linha {lineNum}: Data inválida");
                        skipped++;
                        continue;
                    }
                    if (!TimeOnly.TryParseExact(parts[5]?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    {
                        errors.Add($"Linha {lineNum}: Horário inválido");
                        skipped++;
                        continue;
                    }

                    // Verificar se já existe
                    var exists = await db.Appointments.AnyAsync(a => a.Date == date && a.Time == time);
                    if (exists)
                    {
                        skipped++;
                        continue;
                    }

                    var ap = new Appointment
                    {
                        FullName = fullName,
                        CPF = cpf,
                        Phone = phone,
                        Date = date,
                        Time = time,
                        Taxa = parts[6]?.Trim() ?? "",
                        Category = (parts[7]?.Trim() ?? "").ToUpperInvariant(),
                        Foto = (parts[8]?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ?? false) || (parts[8]?.Trim() == "Sim"),
                        Psicotecnico = (parts[9]?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ?? false) || (parts[9]?.Trim() == "Sim"),
                        Toxicologico = (parts[10]?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ?? false) || (parts[10]?.Trim() == "Sim"),
                        CreatedAt = DateTime.UtcNow
                    };

                    db.Appointments.Add(ap);
                    imported++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Linha {lineNum}: {ex.Message}");
                    skipped++;
                }
            }
        }
        else if (extension == ".xlsx")
        {
            // Processar XLSX
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheets.FirstOrDefault();
            if (ws == null)
            {
                return Results.BadRequest(new { message = "Planilha Excel vazia ou inválida." });
            }

            var rows = ws.RowsUsed().Skip(1); // Pular header
            int rowNum = 2;
            foreach (var row in rows)
            {
                try
                {
                    var fullName = row.Cell(2).GetString().Trim();
                    var cpf = NormalizeCpf(row.Cell(3).GetString());
                    var phone = NormalizePhone(row.Cell(4).GetString());
                    
                    if (string.IsNullOrWhiteSpace(fullName) || cpf.Length != 11)
                    {
                        errors.Add($"Linha {rowNum}: Dados inválidos");
                        skipped++;
                        rowNum++;
                        continue;
                    }

                    if (!DateOnly.TryParseExact(row.Cell(7).GetString().Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        errors.Add($"Linha {rowNum}: Data inválida");
                        skipped++;
                        rowNum++;
                        continue;
                    }

                    if (!TimeOnly.TryParseExact(row.Cell(8).GetString().Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    {
                        errors.Add($"Linha {rowNum}: Horário inválido");
                        skipped++;
                        rowNum++;
                        continue;
                    }

                    // Verificar se já existe
                    var exists = await db.Appointments.AnyAsync(a => a.Date == date && a.Time == time);
                    if (exists)
                    {
                        skipped++;
                        rowNum++;
                        continue;
                    }

                    var ap = new Appointment
                    {
                        FullName = fullName,
                        CPF = cpf,
                        Phone = phone,
                        Date = date,
                        Time = time,
                        Taxa = row.Cell(6).GetString().Trim(),
                        Category = row.Cell(5).GetString().Trim().ToUpperInvariant(),
                        Foto = row.Cell(9).GetString().Trim().Equals("Sim", StringComparison.OrdinalIgnoreCase),
                        Psicotecnico = row.Cell(10).GetString().Trim().Equals("Sim", StringComparison.OrdinalIgnoreCase),
                        Toxicologico = row.Cell(11).GetString().Trim().Equals("Sim", StringComparison.OrdinalIgnoreCase),
                        CreatedAt = DateTime.UtcNow
                    };

                    db.Appointments.Add(ap);
                    imported++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Linha {rowNum}: {ex.Message}");
                    skipped++;
                }
                rowNum++;
            }
        }
        else
        {
            return Results.BadRequest(new { message = "Formato de arquivo não suportado. Use CSV ou XLSX." });
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Importação concluída: {Imported} importados, {Skipped} ignorados, {Errors} erros", imported, skipped, errors.Count);

        return Results.Ok(new 
        { 
            message = $"Importação concluída: {imported} agendamentos importados, {skipped} ignorados.",
            imported,
            skipped,
            errors = errors.Take(50).ToList() // Limitar a 50 erros
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Erro ao importar arquivo");
        return Results.Problem($"Erro ao processar arquivo: {ex.Message}");
    }
});

app.Run();

// ===================== TIPOS (devem vir DEPOIS de todas as instruções de nível superior) =====================
public record AppointmentDto(
    string FullName, string CPF, string? Phone,
    string Date, string Time, string? Taxa, string? Category,
    bool Foto, bool Psicotecnico, bool Toxicologico);

public record RescheduleDto(string Cpf, string Phone, string Date, string Time);
public record CancelDto(string Cpf, string Phone);
public record AdminEditDto(string FullName, string Cpf, string Phone);
public record BlockDayDto(DateTime Date, string Reason);
public record NewTimeDto(DateTime Date, TimeSpan Time, string Category, string FullName, string CPF, string Phone, string Taxa, bool Foto, bool Psicotecnico, bool Toxicologico);
public record DayScheduleDto(int DayOfWeek, string[] TimeSlots);

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<BlockedDay> BlockedDays => Set<BlockedDay>();
    public DbSet<DaySchedule> DaySchedules => Set<DaySchedule>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Appointment>()
            .HasIndex(a => new { a.Date, a.Time })
            .IsUnique();
        modelBuilder.Entity<BlockedDay>()
            .HasIndex(b => b.Date)
            .IsUnique();
        modelBuilder.Entity<DaySchedule>()
            .HasIndex(d => d.DayOfWeek)
            .IsUnique();
    }
}

public class Appointment
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string CPF { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
    public string Taxa { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // A, B, C, D, E, AB, AC, AD, AE
    public bool Foto { get; set; }
    public bool Psicotecnico { get; set; } // opcional
    public bool Toxicologico { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class BlockedDay
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class DaySchedule
{
    public int Id { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public string TimeSlots { get; set; } = string.Empty; // JSON array de horários como "HH:mm"
    public DateTime UpdatedAt { get; set; }
}

