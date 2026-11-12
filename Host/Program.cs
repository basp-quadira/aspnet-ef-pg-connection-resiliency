using Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication
    .CreateBuilder(args);

// Eagerly validate the demo configuration values so boot fails fast on
// out-of-range entries rather than surfacing them on the first request.
_ = builder
    .Configuration
    .ConnectionRatePercent();

_ = builder
    .Configuration
    .CommandRatePercent();

_ = builder
    .Configuration
    .RetryMaxRetryCount();

_ = builder
    .Configuration
    .RetryMaxRetryDelaySeconds();

builder
    .Services
    .AddScoped<SimulatedTransientErrorInterceptor>()
    .AddScoped<SimulatedConnectionFaultInterceptor>()
    .AddDbContext<FooContext>((sp, ef) =>
    {
        const string ConnectionStringName = "Foo";

        var connectionString = builder
            .Configuration
            .GetConnectionString(ConnectionStringName);

        IInterceptor[] interceptors =
        [
            sp.GetRequiredService<SimulatedTransientErrorInterceptor>(),
            sp.GetRequiredService<SimulatedConnectionFaultInterceptor>(),
        ];

        ef.UseNpgsql(
            connectionString,
            pg =>
            {
                var maxRetryCount = builder.Configuration.RetryMaxRetryCount();
                var maxRetryDelay = TimeSpan.FromSeconds(
                    builder
                        .Configuration
                        .RetryMaxRetryDelaySeconds());

                pg.EnableRetryOnFailure(
                    maxRetryCount: maxRetryCount,
                    maxRetryDelay: maxRetryDelay,
                    errorCodesToAdd: null);
            });
        
        ef.AddInterceptors(interceptors);
    });

builder
    .Services
    .AddHealthChecks()
    .AddDbContextCheck<FooContext>()
        //customTestQuery: (context, token) => 
        //    context.Database.CanConnectAsync(token))
    .AddCheck<FooHealthCheck>("foo");

var app = builder
    .Build();

app.MapHealthChecks("/health"); 

app.MapGet(
    "/", 
    () => 
        "Hello World!");

app.MapGet(
    "/items", 
    async (FooContext db) => 
        await db.Items.ToListAsync());

app.MapPost(
    "/items",
    async (FooContext db, Item item) =>
    {
        item.Id = Guid.NewGuid();
        await db.SaveChangesAsync();
        await db.Items.AddAsync(item);
        return Results.Created($"/items/{item.Id}", item);
    });

app.Run();
