using System.Security.Claims;
using System.Text.Json;
using Api.Web.Authorization;
using Api.Web.Consumers;
using Api.Web.Data;
using Api.Web.Hubs;
using Api.Web.Services.Blacklist;
using Casbin;
using Casbin.Persist;
using Casbin.Persist.Adapter.EFCore;
using Casbin.Persist.Adapter.EFCore.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// --- Authentication: Keycloak issues the tokens, this API only validates them ---
var keycloak = builder.Configuration.GetSection("Keycloak");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = keycloak["Authority"];
        options.Audience = keycloak["Audience"];
        options.RequireHttpsMetadata = keycloak.GetValue("RequireHttpsMetadata", true);

        options.Events = new JwtBearerEvents
        {
            // Let SignalR hubs authenticate over the WebSocket query string, since the
            // browser client can't attach an Authorization header to that handshake.
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },

            // Keycloak puts realm roles in a nested "realm_access.roles" claim rather than
            // individual role claims, so flatten them into ClaimTypes.Role for [Authorize]
            // and CasbinAuthorizationHandler to consume.
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is not ClaimsIdentity identity)
                {
                    return Task.CompletedTask;
                }

                var realmAccess = identity.FindFirst("realm_access")?.Value;
                if (string.IsNullOrEmpty(realmAccess))
                {
                    return Task.CompletedTask;
                }

                using var json = JsonDocument.Parse(realmAccess);
                if (json.RootElement.TryGetProperty("roles", out var roles))
                {
                    foreach (var role in roles.EnumerateArray())
                    {
                        var value = role.GetString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            identity.AddClaim(new Claim(ClaimTypes.Role, value));
                        }
                    }
                }

                return Task.CompletedTask;
            }
        };
    });

var sqlConnectionString = builder.Configuration.GetConnectionString("SistemaLPR");

// --- Domain data: Camaras, VehiculosRobados, LecturasHistoricas, Alertas ---
builder.Services.AddDbContext<LprDbContext>(options =>
    options.UseSqlServer(sqlConnectionString, sql => sql.UseNetTopologySuite()));

// --- Authorization: Casbin decides what each role may do ---
builder.Services.AddHttpContextAccessor();

builder.Services.AddDbContext<CasbinDbContext<int>>(options =>
    options.UseSqlServer(sqlConnectionString));

builder.Services.AddEFCoreAdapter<int>();

builder.Services.AddScoped<IEnforcer>(sp =>
{
    var adapter = sp.GetRequiredService<IAdapter>();
    var modelPath = Path.Combine(builder.Environment.ContentRootPath, "Authorization", "rbac_model.conf");
    return new Enforcer(modelPath, adapter);
});

builder.Services.AddScoped<IAuthorizationHandler, CasbinAuthorizationHandler>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Casbin", policy => policy.Requirements.Add(new CasbinRequirement()));
});

// --- Fase 2: SignalR (AlertHub) con backplane de Redis, para escalar el push de alertas a
// más de una instancia de Api.Web ---
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
builder.Services
    .AddSignalR()
    .AddStackExchangeRedis(redisConnectionString);

// --- Fase 2: DotNetCore.CAP/RabbitMQ (reemplaza a MassTransit — ver ImplementersGuide.md §8
// para el porqué). AlertNotificationConsumer se suscribe a "blacklist-hit-saved-event" con su
// propio DefaultGroupName ("api-web"), independiente del grupo de Service.Inference, así que
// ambos reciben su propia copia del mensaje y la empuja por SignalR a AlertHub sin esperar a
// que la persistencia (en Service.Inference) termine ---
builder.Services.AddTransient<AlertNotificationConsumer>();

builder.Services.AddCap(x =>
{
    // CAP usa la misma base "SistemaLPR" para su propio outbox transaccional (tablas
    // cap.Published / cap.Received, creadas automáticamente al arrancar) — no choca con el
    // esquema de LprDbContext ni con casbin_rule.
    x.UseSqlServer(sqlConnectionString!);

    var rabbitMq = builder.Configuration.GetSection("RabbitMq");
    x.UseRabbitMQ(o =>
    {
        o.HostName = rabbitMq["Host"] ?? "localhost";
        o.VirtualHost = rabbitMq["VirtualHost"] ?? "/";
        o.UserName = rabbitMq["Username"] ?? "guest";
        o.Password = rabbitMq["Password"] ?? "guest";
    });

    x.DefaultGroupName = "api-web";
});

// --- Fase 3: alimentación de la lista negra (VehiculosRobados) desde múltiples fuentes que
// traen los mismos datos (API externa, Excel, .txt) — ver ImplementersGuide.md §11.
// IBlacklistImportService concentra la reconciliación por placa (alta/baja/actualización) que
// usan tanto el import manual de archivos (BlacklistController.Import) como la sincronización
// periódica con la fuente externa (ExternalBlacklistSyncService).
builder.Services.AddScoped<IBlacklistImportService, BlacklistImportService>();

// HttpExternalBlacklistSource: GET simple con bearer token estático (ver
// ExternalBlacklistApiOptions — BaseUrl en appsettings.json, BearerToken vía user-secrets/env,
// NUNCA en appsettings.json). AddHttpClient<TInterface, TImplementation> registra un HttpClient
// tipado, con el auth handler inyectando el header en cada request.
builder.Services.Configure<ExternalBlacklistApiOptions>(builder.Configuration.GetSection(ExternalBlacklistApiOptions.SectionName));
builder.Services.AddTransient<ExternalBlacklistAuthHandler>();
builder.Services.AddHttpClient<IExternalBlacklistSource, HttpExternalBlacklistSource>(client =>
    {
        // Timeout corto a propósito: si la red/VPN hacia la API del cliente falla, queremos un
        // error claro y rápido en el log en vez de esperar el default de HttpClient (~100s) en
        // silencio antes de que aparezca cualquier mensaje.
        client.Timeout = TimeSpan.FromSeconds(20);
    })
    .AddHttpMessageHandler<ExternalBlacklistAuthHandler>();
builder.Services.AddHostedService<ExternalBlacklistSyncService>();

var app = builder.Build();

// Apply pending domain migrations, create the Casbin policy table (Casbin ships no
// migrations of its own — EnsureCreated is the adapter's documented setup path), and seed
// the baseline role -> permission matrix. All three are safe to run on every startup.
//
// Order matters here: CasbinDbContext<int>.Database.EnsureCreated() only creates its own
// schema when the physical database has ZERO tables. It shares the "SistemaLPR" database
// with LprDbContext (see appsettings.json), so if LprDbContext's migration ran first and
// created Camaras/VehiculosRobados/etc., EnsureCreated() sees "this database already has
// tables" and silently skips creating "casbin_rule" — the app then crashes the moment
// Casbin tries to load policies (SqlException: Invalid object name 'casbin_rule'). Running
// EnsureCreated() first, while the database is still empty, avoids that trap. Confirmed
// during Fase 1 end-to-end verification (2026-08-18) — see ImplementersGuide.md §8. Note:
// on a database that was already bootstrapped in the old order, swapping this order alone
// will NOT retroactively fix it; the SQL Server volume needs to be reset once.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<CasbinDbContext<int>>().Database.EnsureCreated();
    scope.ServiceProvider.GetRequiredService<LprDbContext>().Database.Migrate();

    var enforcer = scope.ServiceProvider.GetRequiredService<IEnforcer>();
    enforcer.SeedDefaultPolicies();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<AlertHub>("/hubs/alerts");

app.Run();
