using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Service.Inference;
using Service.Inference.Consumers;
using Service.Inference.Data;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<SqlConnectionFactory>();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379"));

// Los suscriptores de CAP (clases con [CapSubscribe]) deben estar registrados en el
// contenedor de DI para que CAP los descubra al arrancar.
builder.Services.AddTransient<BlacklistHitPersistenceConsumer>();
builder.Services.AddTransient<BlacklistEntryAddedConsumer>();
builder.Services.AddTransient<BlacklistEntryRemovedConsumer>();

// PlateReadConsumer YA NO es un suscriptor de CAP (2026-08-18) — se registra como
// BackgroundService normal porque habla RabbitMQ.Client directo, fuera del outbox de CAP. Ver
// el comentario en Consumers/PlateReadConsumer.cs e ImplementersGuide.md §9 para el porqué.
builder.Services.AddHostedService<PlateReadConsumer>();

builder.Services.AddCap(x =>
{
    var sqlConnectionString = builder.Configuration.GetConnectionString("SistemaLPR");
    x.UseSqlServer(sqlConnectionString!);

    var rabbitMq = builder.Configuration.GetSection("RabbitMq");
    x.UseRabbitMQ(o =>
    {
        o.HostName = rabbitMq["Host"] ?? "localhost";
        o.VirtualHost = rabbitMq["VirtualHost"] ?? "/";
        o.UserName = rabbitMq["Username"] ?? "guest";
        o.Password = rabbitMq["Password"] ?? "guest";
    });

    // "Group" distinto por servicio: Service.Inference y Api.Web suscriben ambos a
    // blacklist-hit-saved-event, y necesitan cada uno su propia copia del mensaje (uno
    // persiste, el otro empuja por SignalR) en vez de competir por el mismo mensaje.
    x.DefaultGroupName = "service-inference";
});

// BlacklistCacheService mantiene el set de Redis "blacklist:active-plates" sincronizado con
// VehiculosRobados: carga inicial al arrancar + refresco delta cada 5 min como respaldo.
builder.Services.AddHostedService<BlacklistCacheService>();

var host = builder.Build();
host.Run();
