using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using ClosedXML.Excel;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    Log.Information("Iniciando aplicação AgendamentosApi");

var builder = WebApplication.CreateBuilder(args);
    
    builder.Host.UseSerilog();

var cs = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(cs))
{
    Log.Error("ConnectionStrings:Default não configurada. Defina a string de conexão do Supabase.");
    throw new InvalidOperationException("ConnectionStrings:Default não configurada.");
}

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(cs));
    
builder.Services.AddMemoryCache(options => { options.SizeLimit = 1024; });
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("https://caccoral.site", "http://localhost:5000", "http://localhost:5001", "http://127.0.0.1:5000", "http://127.0.0.1:5001")
               .AllowAnyHeader()
              .AllowAnyMethod();
    });
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 104857600;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "Agendamentos API", Version = "v1" });
    });

var app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
    }

    app.Use(async (context, next) =>
    {
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Append("X-Frame-Options", "DENY");
        context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
        if (context.Request.IsHttps)
        {
            context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        }
        await next();
    });

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
    app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "Agendamentos API v1"); c.RoutePrefix = "swagger"; });
}

    app.UseExceptionHandler(appBuilder =>
    {
        appBuilder.Run(async context =>
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            var exceptionHandlerPathFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
            var exception = exceptionHandlerPathFeature?.Error;
            if (exception != null)
            {
            logger.LogError(exception, "Exceção não tratada: {Message} | Path: {Path}", exception.Message, context.Request.Path);
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Ocorreu um erro interno no servidor.", error = exception.GetType().Name });
            }
        });
    });

app.UseCors();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, " Exceção não tratada: {Message} | Path: {Path}", ex.Message, context.Request.Path);
        if (context.Response.HasStarted)
        {
            throw;
        }
        
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        
        var errorResponse = new
        {
            success = false,
            message = "Erro interno do servidor. Por favor, tente novamente mais tarde.",
            error = app.Environment.IsDevelopment() ? ex.Message : null,
            path = context.Request.Path.Value,
            timestamp = DateTime.UtcNow
        };
        
        await context.Response.WriteAsJsonAsync(errorResponse);
    }
});

static string NormalizeCpf(string? cpf) => cpf is null ? "" : Regex.Replace(cpf, "[^0-9]", "");
static string NormalizePhone(string? p) => p is null ? "" : Regex.Replace(p, "[^0-9]", "");
static string ToLabel(string hhmm) { var p = hhmm.Split(':'); return $"{int.Parse(p[0])}H{(p[1]=="00"?"":p[1])}"; }
// Apenas segundas e terças disponíveis para agendamento; quarta bloqueada
static bool IsAfternoonDay(DayOfWeek d) => d == DayOfWeek.Monday || d == DayOfWeek.Tuesday;

// Data especial liberada em uma quarta-feira (18/02/2026) com horários específicos à tarde
static bool IsSpecialOpenDate(DateOnly d) => d == new DateOnly(2026, 2, 18);
static string[] GetSpecialOpenDateSlots() => new[]{
    "13:30","13:35","13:40","13:45","13:50","13:55",
    "14:00","14:05","14:10","14:15","14:20","14:25","14:30","14:35","14:40","14:45","14:50","14:55",
    "15:00","15:05","15:10","15:15","15:20","15:25","15:30","15:35","15:40","15:45","15:50","15:55",
    "16:00","16:05","16:10","16:15","16:20","16:25","16:30","16:35","16:40","16:45"
};

static string[]? ParseCsvLine(string line)
{
    if (string.IsNullOrWhiteSpace(line)) return null;
    
    var result = new List<string>();
    var current = new System.Text.StringBuilder();
    bool inQuotes = false;
    
    for (int i = 0; i < line.Length; i++)
    {
        char c = line[i];
        
        if (c == '"')
        {
            if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else
            {
                inQuotes = !inQuotes;
            }
        }
        else if (c == ',' && !inQuotes)
        {
            result.Add(current.ToString().Trim());
            current.Clear();
        }
        else
        {
            current.Append(c);
        }
    }
    
    result.Add(current.ToString().Trim());
    
    return result.ToArray();
}
static bool IsMorningDay(DayOfWeek d) => d == DayOfWeek.Thursday;

static bool IsThursdayDisabled(DateOnly date)
{
    var cutoffDate = new DateOnly(2025, 11, 20);
    return date.DayOfWeek == DayOfWeek.Thursday && date >= cutoffDate;
}
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
    // Exceção: liberar somente a quarta-feira 18/02/2026 com horários especiais
    if (IsSpecialOpenDate(d))
        return GetSpecialOpenDateSlots();

    if (IsThursdayDisabled(d))
        return Array.Empty<string>();
    
    return IsAfternoonDay(d.DayOfWeek) ? AfternoonSlots :
           (IsMorningDay(d.DayOfWeek) ? MorningSlots : Array.Empty<string>());
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        db.Database.EnsureCreated();
        
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
        
        using var command = connection.CreateCommand();
        
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS BlockedDays (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Date TEXT NOT NULL UNIQUE,
                Reason TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
        ";
        await command.ExecuteNonQueryAsync();
        
        command.CommandText = @"
            CREATE UNIQUE INDEX IF NOT EXISTS IX_BlockedDays_Date ON BlockedDays(Date);
        ";
        await command.ExecuteNonQueryAsync();
        
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS DaySchedules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DayOfWeek INTEGER NOT NULL UNIQUE,
                TimeSlots TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
        ";
        await command.ExecuteNonQueryAsync();
        
        command.CommandText = @"
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DaySchedules_DayOfWeek ON DaySchedules(DayOfWeek);
        ";
        await command.ExecuteNonQueryAsync();
        
        command.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_Appointments_CPF ON Appointments(CPF);
        ";
        await command.ExecuteNonQueryAsync();
        
        command.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_Appointments_Phone ON Appointments(Phone);
        ";
        await command.ExecuteNonQueryAsync();
        
        command.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_Appointments_Date ON Appointments(Date);
        ";
        await command.ExecuteNonQueryAsync();
        
        command.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_Appointments_CreatedAt ON Appointments(CreatedAt);
        ";
        await command.ExecuteNonQueryAsync();
        
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS AuditLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserEmail TEXT NOT NULL,
                UserName TEXT NOT NULL,
                Action TEXT NOT NULL,
                EntityType TEXT NOT NULL,
                EntityId INTEGER,
                Description TEXT NOT NULL,
                OldValue TEXT,
                NewValue TEXT,
                CreatedAt TEXT NOT NULL
            );
        ";
        await command.ExecuteNonQueryAsync();
        
        command.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_AuditLogs_CreatedAt ON AuditLogs(CreatedAt);
        ";
        await command.ExecuteNonQueryAsync();
        
        command.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_AuditLogs_UserEmail ON AuditLogs(UserEmail);
        ";
        await command.ExecuteNonQueryAsync();
        
        command.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_AuditLogs_EntityType ON AuditLogs(EntityType);
        ";
        await command.ExecuteNonQueryAsync();
        
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Índices de banco de dados criados/verificados com sucesso");
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Erro ao inicializar banco de dados");
    }
}

app.MapGet("/health", () => Results.Text("ok"));

static bool IsAdmin(HttpRequest req, IConfiguration cfg)
{
    var envKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
    var configKey = cfg["ADMIN_KEY"];
    var expectedKey = envKey ?? configKey ?? "cac-coral-drpaulo";
    return req.Query.TryGetValue("key", out var key) && key.ToString() == expectedKey;
}

app.MapGet("/admin", (HttpContext ctx, IWebHostEnvironment env, IConfiguration cfg, ILogger<Program> logger) =>
{
    if (!IsAdmin(ctx.Request, cfg))
    {
        return Results.NotFound();
    }
    
    var adminPaths = new[]
    {
        Path.Combine(env.ContentRootPath, "..", "frontend", "admin", "index.html"),
        Path.Combine(env.ContentRootPath, "frontend", "admin", "index.html"),
        Path.Combine(AppContext.BaseDirectory, "..", "frontend", "admin", "index.html"),
        Path.Combine(AppContext.BaseDirectory, "frontend", "admin", "index.html")
    };
    
    string? html = null;
    foreach (var path in adminPaths)
    {
        var normalizedPath = Path.GetFullPath(path);
        if (File.Exists(normalizedPath))
        {
            html = File.ReadAllText(normalizedPath);
            break;
        }
    }
    
    if (html != null)
    {
        return Results.Content(html, "text/html; charset=utf-8");
    }
    
    return Results.Content("Arquivo admin/index.html não encontrado", "text/plain; charset=utf-8");
});

app.MapGet("/slots", async (
    [FromQuery] string date,
    AppDbContext db,
    ILogger<Program> logger,
    IMemoryCache? cache = null) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            logger.LogWarning("Parâmetro 'date' vazio ou nulo");
            return Results.BadRequest(new { message = "Parâmetro 'date' é obrigatório. Use YYYY-MM-DD." });
        }

        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            logger.LogWarning(" Data inválida recebida: {Date}", date);
            return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });
        }

        bool isThursdayDisabled = IsThursdayDisabled(d);
        logger.LogInformation("Verificação quinta-feira: Data={Date}, DayOfWeek={DayOfWeek}, IsDisabled={IsDisabled}", 
            d, d.DayOfWeek, isThursdayDisabled);
        
        if (isThursdayDisabled)
        {
            logger.LogWarning("QUINTA-FEIRA DESABILITADA: {Date} (dia {DayOfWeek}) - Retornando array vazio. Data limite: 20/11/2025", 
                d, d.DayOfWeek);
            return Results.Ok(new { date = d.ToString("yyyy-MM-dd"), slots = Array.Empty<object>() });
        }

        string[] allowed = Array.Empty<string>();
        
        // Para a data especial, não usar cache por dia da semana para não afetar outras quartas
        if (IsSpecialOpenDate(d))
        {
            allowed = GetAllowedSlots(d);
            logger.LogInformation("Usando horários ESPECIAIS para data {Date}: {Count} slots", d, allowed.Length);
        }
        else
        {
            string cacheKey = $"slots_allowed_{d.DayOfWeek}";
            if (cache != null && cache.TryGetValue<string[]>(cacheKey, out var cachedAllowed) && cachedAllowed != null)
            {
                allowed = cachedAllowed;
                logger.LogInformation("Cache HIT para horários do dia {DayOfWeek}", d.DayOfWeek);
            }
            else
            {
                var customSchedule = await db.DaySchedules
                    .Where(s => s.DayOfWeek == d.DayOfWeek)
                    .FirstOrDefaultAsync();
                
                logger.LogInformation("Verificando horários para {DayOfWeek} ({Date}): CustomSchedule={HasCustom}, TimeSlots={TimeSlots}", 
                    d.DayOfWeek, d, customSchedule != null, customSchedule?.TimeSlots?.Substring(0, Math.Min(50, customSchedule.TimeSlots?.Length ?? 0)) ?? "null");
                
                if (customSchedule != null && 
                    !string.IsNullOrWhiteSpace(customSchedule.TimeSlots) && 
                    customSchedule.TimeSlots.Trim() != "[]" &&
                    customSchedule.TimeSlots.Trim() != "null")
                {
                    var customSlots = System.Text.Json.JsonSerializer.Deserialize<string[]>(customSchedule.TimeSlots);
                    if (customSlots != null && customSlots.Length > 0 && customSlots.Any(s => !string.IsNullOrWhiteSpace(s)))
                    {
                        allowed = customSlots.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                        logger.LogInformation("✓ CUSTOMIZADOS: {DayOfWeek} ({Date}) = {Count} slots", 
                            d.DayOfWeek, d, allowed.Length);
                    }
                    else
                    {
                        allowed = GetAllowedSlots(d);
                        logger.LogWarning("⚠ Custom vazio/null, usando PADRÃO: {DayOfWeek} ({Date}) = {Count} slots", 
                            d.DayOfWeek, d, allowed.Length);
                    }
                }
                else
                {
                    allowed = GetAllowedSlots(d);
                    logger.LogInformation("→ PADRÃO: {DayOfWeek} ({Date}) = {Count} slots (sem customização no banco)", 
                        d.DayOfWeek, d, allowed.Length);
                }
                if (cache != null)
                {
                    var cacheOptions = new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2),
                        SlidingExpiration = TimeSpan.FromMinutes(1),
                        Size = 1
                    };
                    cache.Set(cacheKey, allowed, cacheOptions);
                    logger.LogInformation("Cache SET para horários do dia {DayOfWeek}", d.DayOfWeek);
                }
            }
        }

        if (allowed == null || allowed.Length == 0)
            return Results.Ok(new { date = d.ToString("yyyy-MM-dd"), slots = Array.Empty<object>() });
        bool isBlocked = await db.BlockedDays.AnyAsync(b => b.Date == d);
        
        if (isBlocked)
        {
            logger.LogInformation("🚫 BLOQUEADO: {Date} - Todos os slots serão marcados como bloqueados", d);
        }
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
                .AsNoTracking()
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

        logger.LogInformation(" Retornando {Count} slots para data {Date}", slots.Length, d);
        return Results.Ok(new { date = d.ToString("yyyy-MM-dd"), slots });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, " Erro ao processar requisição /slots para data {Date}", date);
        return Results.Problem(
            detail: $"Erro ao processar requisição: {ex.Message}",
            statusCode: 500
        );
    }
});
app.MapPost("/appointments", async ([FromBody] AppointmentDto input, AppDbContext db) =>
{
    if (!ValidationHelpers.IsValidName(input.FullName))
        return Results.BadRequest(new { message = "Nome é obrigatório e deve ter entre 3 e 200 caracteres." });

    if (!ValidationHelpers.IsValidCpf(input.CPF))
        return Results.BadRequest(new { message = "CPF inválido. Deve conter 11 dígitos numéricos." });

    if (!ValidationHelpers.IsValidDate(input.Date, out var d))
        return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });
    // Bloquear quintas (regra global) e quartas, exceto a data especial liberada
    if ((IsThursdayDisabled(d) || d.DayOfWeek == DayOfWeek.Wednesday) && !IsSpecialOpenDate(d))
        return Results.BadRequest(new { message = "Não atendemos no dia selecionado." });

    if (!ValidationHelpers.IsValidTime(input.Time, out var t))
        return Results.BadRequest(new { message = "Horário inválido. Use HH:mm." });

    var cat = (input.Category ?? "").Trim().ToUpperInvariant();
    if (!ValidationHelpers.IsValidCategory(cat))
        return Results.BadRequest(new { message = "Categoria inválida. Use A, B, C, D, E, AB, AC, AD ou AE." });
    var isBlocked = await db.BlockedDays.AnyAsync(b => b.Date == d);
    if (isBlocked)
        return Results.BadRequest(new { message = "Este dia está bloqueado para agendamentos." });
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
    if (ValidationHelpers.NeedsToxicologico(cat))
    {
        if (!input.Foto || !input.Toxicologico)
            return Results.BadRequest(new { message = "Para C, D, E, AC, AD e AE é obrigatório confirmar Foto e Toxicológico (validade 3 meses)." });
    }
    else
    {
        if (!input.Foto)
            return Results.BadRequest(new { message = "Para A, B e AB é obrigatório confirmar Foto." });
    }
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
        Psicotecnico = input.Psicotecnico,
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
app.MapGet("/appointments", async ([FromQuery] string? date, AppDbContext db) =>
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
app.MapGet("/appointments/{id:int}/confirmation", async (int id, AppDbContext db) =>
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
app.MapGet("/questionario/download", (IWebHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "Assets", "questionario.pdf");
    if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "Assets", "questionario.pdf");
    if (!File.Exists(path)) return Results.NotFound(new { message = "Arquivo não encontrado em 'Assets/questionario.pdf'." });
    var bytes = File.ReadAllBytes(path);
    return Results.File(bytes, "application/pdf", "questionario.pdf");
});
app.MapGet("/appointments/by-cpf", async ([FromQuery] string cpf, [FromQuery] string phone, AppDbContext db, ILogger<Program> logger) =>
{
    if (!ValidationHelpers.IsValidCpf(cpf))
        return Results.BadRequest(new { message = "CPF inválido. Deve conter 11 dígitos numéricos." });

    if (!ValidationHelpers.IsValidPhone(phone))
        return Results.BadRequest(new { message = "Telefone inválido. Deve conter entre 8 e 15 dígitos numéricos." });

    var cpfNorm = NormalizeCpf(cpf);
    var phoneNorm = NormalizePhone(phone);
    logger.LogInformation(" Buscando agendamento - CPF: {Cpf}, Phone: {Phone}", 
        DataSanitizer.MaskCpf(cpfNorm), DataSanitizer.MaskPhone(phoneNorm));
    var allCpf = await db.Appointments
        .AsNoTracking()
        .Where(a => a.CPF == cpfNorm)
        .OrderBy(a => a.Date).ThenBy(a => a.Time)
        .ToListAsync();
    logger.LogInformation("📋 Encontrados {Count} agendamentos para CPF {Cpf}", allCpf.Count, DataSanitizer.MaskCpf(cpfNorm));
    var all = allCpf.Where(a => NormalizePhone(a.Phone) == phoneNorm).ToList();
    
    logger.LogInformation("Após filtrar por telefone: {Count} agendamentos", all.Count);
    
    if (all.Count == 0)
    {
        logger.LogWarning(" Nenhum agendamento encontrado para CPF {Cpf} e telefone {Phone}", 
            DataSanitizer.MaskCpf(cpfNorm), DataSanitizer.MaskPhone(phoneNorm));
        return Results.NotFound(new { message = "Nenhum agendamento encontrado para este CPF/Telefone." });
    }

    var today = DateOnly.FromDateTime(DateTime.Now);
    var nowT  = TimeOnly.FromDateTime(DateTime.Now);
    var ap = all.FirstOrDefault(a => a.Date > today || (a.Date == today && a.Time > nowT)) ?? all.Last();
    
    if (ap == null)
    {
        return Results.NotFound(new { message = "Agendamento não encontrado." });
    }

    logger.LogInformation(" Agendamento encontrado: ID {Id}, Data: {Date}, Hora: {Time}", ap.Id, ap.Date, ap.Time);

    return Results.Ok(new {
        id = ap.Id,
        fullName = ap.FullName,
        cpf = ap.CPF,
        phone = ap.Phone,
        date = ap.Date.ToString("yyyy-MM-dd"),
        time = ap.Time.ToString("HH:mm"),
        category = ap.Category,
        taxa = ap.Taxa,
        foto = ap.Foto,
        psicotecnico = ap.Psicotecnico,
        toxicologico = ap.Toxicologico,
        createdAt = ap.CreatedAt
    });
});
app.MapPost("/appointments/by-cpf/reschedule", async ([FromBody] RescheduleDto input, AppDbContext db, ILogger<Program> logger) =>
{
    if (!ValidationHelpers.IsValidCpf(input.Cpf))
        return Results.BadRequest(new { message = "CPF inválido. Deve conter 11 dígitos numéricos." });

    if (!ValidationHelpers.IsValidPhone(input.Phone))
        return Results.BadRequest(new { message = "Telefone inválido. Deve conter entre 8 e 15 dígitos numéricos." });

    if (!ValidationHelpers.IsValidDate(input.Date, out var newDate))
        return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });
    // Bloquear quintas (regra global) e quartas, exceto a data especial liberada
    if ((IsThursdayDisabled(newDate) || newDate.DayOfWeek == DayOfWeek.Wednesday) && !IsSpecialOpenDate(newDate))
        return Results.BadRequest(new { message = "Não atendemos no dia selecionado." });
    
    if (!ValidationHelpers.IsValidTime(input.Time, out var newTime))
        return Results.BadRequest(new { message = "Horário inválido. Use HH:mm." });
    var isBlocked = await db.BlockedDays.AnyAsync(b => b.Date == newDate);
    if (isBlocked)
        return Results.BadRequest(new { message = "Este dia está bloqueado para agendamentos." });

    var cpf = NormalizeCpf(input.Cpf);
    var phone = NormalizePhone(input.Phone);
    logger.LogInformation(" Buscando agendamento para reagendamento - CPF: {Cpf}, Phone: {Phone}", 
        DataSanitizer.MaskCpf(cpf), DataSanitizer.MaskPhone(phone));
    var allCpf = await db.Appointments
        .AsNoTracking()
        .Where(a => a.CPF == cpf)
        .OrderBy(a => a.Date).ThenBy(a => a.Time)
        .ToListAsync();
    logger.LogInformation("📋 Encontrados {Count} agendamentos para CPF {Cpf}", allCpf.Count, DataSanitizer.MaskCpf(cpf));
    var all = allCpf.Where(a => NormalizePhone(a.Phone) == phone).ToList();
    
    logger.LogInformation("📞 Após filtrar por telefone: {Count} agendamentos", all.Count);
    
    if (all.Count == 0)
    {
        logger.LogWarning(" Nenhum agendamento encontrado para CPF {Cpf} e telefone {Phone}", 
            DataSanitizer.MaskCpf(cpf), DataSanitizer.MaskPhone(phone));
        return Results.NotFound(new { message = "Nenhum agendamento encontrado para este CPF/Telefone." });
    }

    var today = DateOnly.FromDateTime(DateTime.Now);
    var nowT  = TimeOnly.FromDateTime(DateTime.Now);
    var ap = all.FirstOrDefault(a => a.Date > today || (a.Date == today && a.Time > nowT)) ?? all.Last();
    
    if (ap == null)
    {
        logger.LogError(" Erro: agendamento encontrado mas ap é null");
        return Results.Problem("Erro ao localizar agendamento.");
    }

    logger.LogInformation(" Agendamento encontrado: ID {Id}, Data: {Date}, Hora: {Time}", ap.Id, ap.Date, ap.Time);
    db.ChangeTracker.Clear();
    var apToUpdate = await db.Appointments.FirstOrDefaultAsync(a => a.Id == ap.Id);
    if (apToUpdate == null)
    {
        logger.LogError(" Erro: não foi possível carregar agendamento ID {Id} para atualização", ap.Id);
        return Results.Problem("Erro ao carregar agendamento para atualização.");
    }
    
    logger.LogInformation("📌 Entidade carregada para atualização: ID {Id}, Estado: {State}", 
        apToUpdate.Id, db.Entry(apToUpdate).State);
    string[] allowed;
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
    {
        logger.LogWarning(" Horário {Time} não permitido para dia {Date}", input.Time, newDate);
        return Results.BadRequest(new { message = "Este horário não é permitido para este dia." });
    }
    var conflict = await db.Appointments.AnyAsync(a => a.Id != apToUpdate.Id && a.Date == newDate && a.Time == newTime);
    if (conflict)
    {
        logger.LogWarning(" Conflito: horário {Time} já reservado para {Date}", newTime, newDate);
        return Results.Conflict(new { message = "Este horário já está reservado." });
    }
    var oldDate = apToUpdate.Date;
    var oldTime = apToUpdate.Time;

    logger.LogInformation("📝 Atualizando agendamento ID {Id}: {OldDate} {OldTime} -> {NewDate} {NewTime}", 
        apToUpdate.Id, oldDate, oldTime, newDate, newTime);
    apToUpdate.Date = newDate;
    apToUpdate.Time = newTime;
    db.Entry(apToUpdate).Property(a => a.Date).IsModified = true;
    db.Entry(apToUpdate).Property(a => a.Time).IsModified = true;

    try 
    { 
        var changes = await db.SaveChangesAsync();
        logger.LogInformation("Salvamento concluído: {Changes} alterações salvas", changes);
        logger.LogInformation(" Reagendamento realizado com sucesso: ID {Id}", apToUpdate.Id);
        logger.LogInformation("   📅 Horário ANTIGO liberado: {OldDate} às {OldTime}", oldDate, oldTime);
        logger.LogInformation("   📅 Horário NOVO ocupado: {NewDate} às {NewTime}", newDate, newTime);
        var oldSlotStillTaken = await db.Appointments.AnyAsync(a => a.Date == oldDate && a.Time == oldTime && a.Id != apToUpdate.Id);
        if (oldSlotStillTaken)
        {
            logger.LogWarning(" Atenção: Horário antigo {OldDate} {OldTime} ainda está ocupado por outro agendamento", oldDate, oldTime);
        }
        else
        {
            logger.LogInformation("   ✓ Horário antigo {OldDate} {OldTime} está livre", oldDate, oldTime);
        }
        var newSlotTaken = await db.Appointments.AnyAsync(a => a.Date == newDate && a.Time == newTime && a.Id == apToUpdate.Id);
        if (newSlotTaken)
        {
            logger.LogInformation("   ✓ Novo horário {NewDate} {NewTime} está ocupado pelo agendamento ID {Id}", newDate, newTime, apToUpdate.Id);
        }
        else
        {
            logger.LogError("    ERRO: Novo horário {NewDate} {NewTime} não foi ocupado corretamente!", newDate, newTime);
        }
    }
    catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE") == true) 
    {
        logger.LogError(ex, " Erro de conflito UNIQUE ao salvar reagendamento");
        return Results.Conflict(new { message = "Conflito de horário. Tente outro." });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, " Erro ao salvar reagendamento");
        return Results.Problem($"Erro ao salvar reagendamento: {ex.Message}");
    }
    return Results.Ok(new { 
        message = "Reagendamento realizado com sucesso.", 
        id = apToUpdate.Id,
        oldDate = oldDate.ToString("yyyy-MM-dd"),
        oldTime = oldTime.ToString("HH:mm"),
        newDate = newDate.ToString("yyyy-MM-dd"),
        newTime = newTime.ToString("HH:mm"),
        fullName = apToUpdate.FullName,
        cpf = apToUpdate.CPF,
        phone = apToUpdate.Phone,
        category = apToUpdate.Category
    });
});

app.MapPost("/appointments/by-cpf/cancel", async ([FromBody] CancelDto input, AppDbContext db) =>
{
    if (!ValidationHelpers.IsValidCpf(input.Cpf))
        return Results.BadRequest(new { message = "CPF inválido. Deve conter 11 dígitos numéricos." });

    if (!ValidationHelpers.IsValidPhone(input.Phone))
        return Results.BadRequest(new { message = "Telefone inválido. Deve conter entre 8 e 15 dígitos numéricos." });

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


app.MapGet("/admin/appointments", async (HttpContext ctx, [FromQuery] string date, AppDbContext db, IConfiguration cfg) =>
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

app.MapMethods("/admin/appointments/{id:int}", new[] { "PATCH" }, async (
    HttpContext ctx,
    int id,
    [FromBody] AdminEditDto input,
    AppDbContext db,
    IConfiguration cfg,
    ILogger<Program> logger) =>
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

app.MapGet("/reports/daily", async (HttpContext ctx, [FromQuery] string date, AppDbContext db, IConfiguration cfg) =>
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

app.MapGet("/backup", async (AppDbContext db) =>
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
app.MapPost("/admin/block-day", async (HttpContext ctx, [FromBody] BlockDayDto input, AppDbContext db, IConfiguration cfg, ILogger<Program> logger) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();
    DateOnly d;
    if (input.Date.Kind == DateTimeKind.Utc)
    {
        d = DateOnly.FromDateTime(input.Date.ToLocalTime().Date);
    }
    else
    {
        d = DateOnly.FromDateTime(input.Date.Date);
    }
    if (d == default)
    {
        return Results.BadRequest(new { message = "Data inválida. Use YYYY-MM-DD." });
    }

    try
    {
        var existing = await db.BlockedDays.FirstOrDefaultAsync(b => b.Date == d);
        if (existing != null)
        {
            return Results.Ok(new { message = "Data já está bloqueada." });
        }
        var blockedDay = new BlockedDay
        {
            Date = d,
            Reason = input.Reason ?? "Bloqueado pelo administrador",
            CreatedAt = DateTime.UtcNow
        };
        db.BlockedDays.Add(blockedDay);
        await db.SaveChangesAsync();
        logger.LogInformation("Data {Date} bloqueada com sucesso. Data salva: {SavedDate}", d, blockedDay.Date);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }

    return Results.Ok(new { message = "Data bloqueada com sucesso. Todos os horários aparecerão como ocupados." });
});

app.MapPost("/admin/unblock-day", async (HttpContext ctx, [FromBody] BlockDayDto input, AppDbContext db, IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();
    DateOnly d;
    if (input.Date.Kind == DateTimeKind.Utc)
    {
        d = DateOnly.FromDateTime(input.Date.ToLocalTime().Date);
    }
    else
    {
        d = DateOnly.FromDateTime(input.Date.Date);
    }
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
app.MapGet("/admin/blocked-days", async (HttpContext ctx, AppDbContext db, IConfiguration cfg, ILogger<Program> logger) =>
{
    logger.LogInformation("Requisição recebida em /admin/blocked-days. Query key: {QueryKey}, Header key: {HeaderKey}", 
        ctx.Request.Query["key"].ToString(), ctx.Request.Headers["X-Admin-Key"].ToString());
    
    if (!IsAdmin(ctx.Request, cfg))
    {
        var cfgKey = cfg["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY") ?? "troque-esta-chave";
        logger.LogWarning("Tentativa de acesso não autorizado ao endpoint /admin/blocked-days. Key esperada: {ExpectedKey}, Key recebida: {ReceivedKey}", 
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
app.MapGet("/admin/day-schedules", async (HttpContext ctx, AppDbContext db, IConfiguration cfg) =>
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

app.MapGet("/admin/day-schedule/{dayOfWeek:int}", async (HttpContext ctx, int dayOfWeek, AppDbContext db, IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();
    if (dayOfWeek < 0 || dayOfWeek > 6) return Results.BadRequest(new { message = "Dia da semana inválido (0-6)." });

    var schedule = await db.DaySchedules.FirstOrDefaultAsync(s => s.DayOfWeek == (DayOfWeek)dayOfWeek);
    if (schedule == null || string.IsNullOrWhiteSpace(schedule.TimeSlots))
    {
        var testDate = DateOnly.FromDateTime(DateTime.Now.AddDays((dayOfWeek - (int)DateTime.Now.DayOfWeek + 7) % 7));
        var defaultSlots = GetAllowedSlots(testDate);
        return Results.Ok(new {
            dayOfWeek = dayOfWeek,
            dayName = ((DayOfWeek)dayOfWeek).ToString(),
            timeSlots = defaultSlots,
            isCustom = false
        });
    }
    var customSlots = System.Text.Json.JsonSerializer.Deserialize<string[]>(schedule.TimeSlots);
    if (customSlots == null || customSlots.Length == 0)
    {
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

app.MapPost("/admin/day-schedule", async (HttpContext ctx, [FromBody] DayScheduleDto input, AppDbContext db, IConfiguration cfg) =>
{
    if (!IsAdmin(ctx.Request, cfg)) return Results.NotFound();
    if (input.DayOfWeek < 0 || input.DayOfWeek > 6)
        return Results.BadRequest(new { message = "Dia da semana inválido (0-6)." });

    try
    {
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
        
        db.ChangeTracker.Clear();
        var savedSchedule = await db.DaySchedules
            .Where(s => s.DayOfWeek == (DayOfWeek)input.DayOfWeek)
            .FirstOrDefaultAsync();
            
        if (savedSchedule != null)
        {
            var savedSlots = System.Text.Json.JsonSerializer.Deserialize<string[]>(savedSchedule.TimeSlots) ?? Array.Empty<string>();
            logger.LogInformation(" SALVO E VERIFICADO: {DayOfWeek} = {Count} slots", 
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
            logger.LogError(" ERRO: Não foi possível verificar salvamento para {DayOfWeek}", (DayOfWeek)input.DayOfWeek);
            return Results.Problem("Horários podem não ter sido salvos corretamente.");
        }
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapDelete("/admin/day-schedule/{dayOfWeek:int}", async (HttpContext ctx, int dayOfWeek, AppDbContext db, IConfiguration cfg) =>
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
app.MapGet("/admin/verify-schedule/{dayOfWeek:int}", async (HttpContext ctx, int dayOfWeek, AppDbContext db, IConfiguration cfg, ILogger<Program> logger) =>
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
app.MapPost("/admin/add-time", async (HttpContext ctx, [FromBody] NewTimeDto input, AppDbContext db, IConfiguration cfg) =>
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
        Psicotecnico = input.Psicotecnico,
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
app.MapPost("/admin/import", async (HttpContext ctx, AppDbContext db, IConfiguration cfg, ILogger<Program> logger) =>
{
    return await ProcessImport(ctx, db, cfg, logger);
});
static async Task<IResult> ProcessImport(HttpContext ctx, AppDbContext db, IConfiguration cfg, ILogger<Program> logger)
{
    try
    {
        logger.LogInformation("Requisição de importação recebida");
        
        if (!IsAdmin(ctx.Request, cfg))
        {
            logger.LogWarning("Tentativa de importação sem autorização");
            return Results.Unauthorized();
        }

        logger.LogInformation("Lendo formulário...");
        ctx.Request.EnableBuffering();
        
        var form = await ctx.Request.ReadFormAsync();
        
        logger.LogInformation("Arquivos recebidos: {Count}", form.Files.Count);
        foreach (var f in form.Files)
        {
            logger.LogInformation("Arquivo: {Name}, {Length} bytes, {ContentType}", f.Name, f.Length, f.ContentType);
        }
        
        var file = form.Files.FirstOrDefault(f => f.Name == "file");
        
        if (file == null || file.Length == 0)
        {
            logger.LogWarning("Nenhum arquivo válido encontrado");
            return Results.BadRequest(new { message = "Nenhum arquivo enviado ou arquivo vazio." });
        }
        
        logger.LogInformation("Processando arquivo: {FileName}, {Length} bytes", file.FileName, file.Length);

        var errors = new List<string>();
        var imported = 0;
        var skipped = 0;

        try
        {
            using var stream = file.OpenReadStream();
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            if (extension == ".csv")
            {
                using var reader = new StreamReader(stream);
                var headerLine = await reader.ReadLineAsync();
                if (headerLine == null)
                {
                    return Results.BadRequest(new { message = "Arquivo CSV vazio ou inválido." });
                }
                string? line;
                int lineNum = 1;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lineNum++;
                    try
                    {
                        var parts = ParseCsvLine(line);
                        if (parts == null || parts.Length < 11) 
                        {
                            errors.Add($"Linha {lineNum}: Campos insuficientes (esperado: 11, encontrado: {(parts?.Length ?? 0)})");
                            skipped++;
                            continue;
                        }
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
                using var wb = new XLWorkbook(stream);
                var ws = wb.Worksheets.FirstOrDefault();
                if (ws == null)
                {
                    return Results.BadRequest(new { message = "Planilha Excel vazia ou inválida." });
                }

                var rows = ws.RowsUsed().Skip(1);
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
                errors = errors.Take(50).ToList()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao importar arquivo: {Message}, StackTrace: {StackTrace}", ex.Message, ex.StackTrace);
            return Results.Problem($"Erro ao processar arquivo: {ex.Message}");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Erro geral na importação: {Message}", ex.Message);
        return Results.Problem($"Erro ao processar requisição: {ex.Message}");
    }
}
app.UseDefaultFiles();
app.UseStaticFiles();

Log.Information("Aplicação configurada e pronta para receber requisições");

app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Aplicação encerrada inesperadamente");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
static class DataSanitizer
{
    public static string MaskCpf(string? cpf)
    {
        if (string.IsNullOrWhiteSpace(cpf)) return "***";
        var normalized = Regex.Replace(cpf, "[^0-9]", "");
        if (normalized.Length < 4) return "***";
        return $"***{normalized.Substring(normalized.Length - 3)}";
    }
    public static string MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "***";
        var normalized = Regex.Replace(phone, "[^0-9]", "");
        if (normalized.Length < 4) return "***";
        return $"***{normalized.Substring(normalized.Length - 4)}";
    }
    public static string SanitizeLogMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;
        message = Regex.Replace(message, @"\b\d{11}\b", m => MaskCpf(m.Value));
        message = Regex.Replace(message, @"\b\d{10,11}\b", m => MaskPhone(m.Value));
        
        return message;
    }
}
static class ValidationHelpers
{
    private static string NormalizeCpf(string? cpf) => cpf is null ? "" : Regex.Replace(cpf, "[^0-9]", "");
    private static string NormalizePhone(string? p) => p is null ? "" : Regex.Replace(p, "[^0-9]", "");
    public static bool IsValidCpf(string? cpf)
    {
        if (string.IsNullOrWhiteSpace(cpf)) return false;
        var normalized = NormalizeCpf(cpf);
        return normalized.Length == 11 && normalized.All(char.IsDigit);
    }
    public static bool IsValidPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return false;
        var normalized = NormalizePhone(phone);
        return normalized.Length >= 8 && normalized.Length <= 15 && normalized.All(char.IsDigit);
    }
    public static bool IsValidDate(string? date, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(date)) return false;
        return DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }
    public static bool IsValidTime(string? time, out TimeOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(time)) return false;
        return TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }
    public static bool IsValidCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        var validCategories = new HashSet<string> { "A", "B", "C", "D", "E", "AB", "AC", "AD", "AE" };
        return validCategories.Contains(category.Trim().ToUpperInvariant());
    }
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        return trimmed.Length >= 3 && trimmed.Length <= 200;
    }
    public static bool NeedsToxicologico(string category)
    {
        var needsTox = new HashSet<string> { "C", "D", "E", "AC", "AD", "AE" };
        return needsTox.Contains(category.Trim().ToUpperInvariant());
    }
}

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
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Appointment>()
            .HasIndex(a => new { a.Date, a.Time })
            .IsUnique();
        modelBuilder.Entity<Appointment>()
            .HasIndex(a => a.CPF)
            .HasDatabaseName("IX_Appointments_CPF");
        
        modelBuilder.Entity<Appointment>()
            .HasIndex(a => a.Phone)
            .HasDatabaseName("IX_Appointments_Phone");
        
        modelBuilder.Entity<Appointment>()
            .HasIndex(a => a.Date)
            .HasDatabaseName("IX_Appointments_Date");
        
        modelBuilder.Entity<Appointment>()
            .HasIndex(a => a.CreatedAt)
            .HasDatabaseName("IX_Appointments_CreatedAt");
        
        modelBuilder.Entity<BlockedDay>()
            .HasIndex(b => b.Date)
            .IsUnique();
        
        modelBuilder.Entity<DaySchedule>()
            .HasIndex(d => d.DayOfWeek)
            .IsUnique();
        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.CreatedAt)
            .HasDatabaseName("IX_AuditLogs_CreatedAt");
        
        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.UserEmail)
            .HasDatabaseName("IX_AuditLogs_UserEmail");
        
        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.EntityType)
            .HasDatabaseName("IX_AuditLogs_EntityType");
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
    public string Category { get; set; } = string.Empty;
    public bool Foto { get; set; }
    public bool Psicotecnico { get; set; }
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
    public string TimeSlots { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

public class AuditLog
{
    public int Id { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public int? EntityId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime CreatedAt { get; set; }
}