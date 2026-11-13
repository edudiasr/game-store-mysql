using System.Data;
using System.Text.Json;
using Dapper;
using GameStore.Api.Db;
using GameStore.Api.Payments;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace GameStore.Api.Endpoints;

// Os DTOs lidos do banco são records com propriedades (e não posicionais) porque o MySQL
// devolve colunas UNSIGNED como uint/ulong; o Dapper converte o tipo ao preencher propriedade,
// mas no construtor exige o tipo exato.
public sealed record ProductDto
{
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public int PriceCents { get; init; }
    public int Coins { get; init; }
    public int? Stock { get; init; }
}

public sealed record OrderItemDto
{
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
    public int Quantity { get; init; }
    public int UnitPriceCents { get; init; }
}

public sealed record OrderDto
{
    public long Id { get; init; }
    public string SteamId { get; init; } = "";
    public string Status { get; init; } = "";
    public int SubtotalCents { get; init; }
    public int DiscountCents { get; init; }
    public int TotalCents { get; init; }
    public string? CouponCode { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? PaidAt { get; init; }
    public IReadOnlyList<OrderItemDto> Items { get; init; } = [];
}

public sealed record WalletEntryDto
{
    public long Id { get; init; }
    public int Amount { get; init; }
    public string Reason { get; init; } = "";
    public long? OrderId { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record SalesDay
{
    public DateTime Day { get; init; }
    public int Orders { get; init; }
    public long RevenueCents { get; init; }
    public long DiscountCents { get; init; }
}

public sealed record TopProduct
{
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
    public long Units { get; init; }
    public long GrossCents { get; init; }
}

internal sealed record WalletHead
{
    public long CustomerId { get; init; }
    public string Nickname { get; init; } = "";
    public long Balance { get; init; }
}

public sealed record WalletDto(string SteamId, string Nickname, long Balance, IReadOnlyList<WalletEntryDto> Recent);
public sealed record NewCustomer(string SteamId, string Nickname, string? Email);
public sealed record CartItem(string Sku, int Qty);
public sealed record NewOrder(string SteamId, List<CartItem> Items, string? Coupon);
public sealed record SpendRequest(int Amount);
public sealed record PaymentNotice(long OrderId, string Provider, string Reference, int AmountCents);

public sealed class PaymentOptions
{
    public string WebhookSecret { get; set; } = "";
}

public static class StoreEndpoints
{
    public static IEndpointRouteBuilder MapStoreEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/products", ListProducts).WithTags("Catálogo").WithSummary("Produtos à venda");

        api.MapPost("/customers", CreateCustomer).WithTags("Clientes").WithSummary("Cadastra um cliente pelo SteamID");
        api.MapGet("/customers/{steamId}/wallet", GetWallet).WithTags("Clientes").WithSummary("Saldo e últimos lançamentos da carteira");
        api.MapPost("/customers/{steamId}/wallet/spend", Spend).WithTags("Clientes").WithSummary("Gasta moedas da carteira");

        api.MapPost("/orders", PlaceOrder).WithTags("Pedidos").WithSummary("Cria um pedido (reserva o estoque)");
        api.MapGet("/orders/{id:long}", GetOrder).WithTags("Pedidos").WithSummary("Detalhes de um pedido");
        api.MapPost("/orders/{id:long}/cancel", CancelOrder).WithTags("Pedidos").WithSummary("Cancela pedido pendente e devolve o estoque");

        api.MapPost("/payments/webhook", PaymentWebhook).WithTags("Pagamentos")
            .WithSummary("Notificação do provedor de pagamento (assinada com HMAC-SHA256 no header X-Signature)");

        api.MapGet("/reports/sales", SalesReport).WithTags("Relatórios").WithSummary("Vendas por dia");
        api.MapGet("/reports/top-products", TopProducts).WithTags("Relatórios").WithSummary("Produtos mais vendidos");
        return app;
    }

    private static async Task<IResult> ListProducts(MySqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<ProductDto>("""
            SELECT sku, name, kind, price_cents AS PriceCents, coins, stock
            FROM products WHERE active ORDER BY kind, price_cents
            """);
        return TypedResults.Ok(rows);
    }

    private static async Task<IResult> CreateCustomer(NewCustomer body, MySqlDataSource db)
    {
        if (!IsSteamId(body.SteamId)) return Invalid("steamId", "SteamID64 tem 17 dígitos");
        if (string.IsNullOrWhiteSpace(body.Nickname) || body.Nickname.Length > 64) return Invalid("nickname", "de 1 a 64 caracteres");

        return await Guard(async () =>
        {
            await using var conn = await db.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO customers (steam_id, nickname, email) VALUES (@SteamId, @Nickname, @Email)",
                new { body.SteamId, Nickname = body.Nickname.Trim(), Email = string.IsNullOrWhiteSpace(body.Email) ? null : body.Email.Trim() });
            return TypedResults.Created($"/api/customers/{body.SteamId}/wallet", new { body.SteamId });
        });
    }

    private static async Task<IResult> GetWallet(string steamId, MySqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        var head = await conn.QuerySingleOrDefaultAsync<WalletHead>(
            "SELECT customer_id AS CustomerId, nickname, balance FROM v_wallet_balance WHERE steam_id = @steamId", new { steamId });
        if (head is null) return TypedResults.NotFound();

        var recent = await conn.QueryAsync<WalletEntryDto>("""
            SELECT id, amount, reason, order_id AS OrderId, created_at AS CreatedAt
            FROM wallet_entries WHERE customer_id = @CustomerId ORDER BY id DESC LIMIT 20
            """, new { head.CustomerId });
        return TypedResults.Ok(new WalletDto(steamId, head.Nickname, head.Balance, recent.AsList()));
    }

    private static async Task<IResult> Spend(string steamId, SpendRequest body, MySqlDataSource db)
    {
        if (body.Amount <= 0) return Invalid("amount", "precisa ser maior que zero");
        return await Guard(async () =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var p = new DynamicParameters(new { p_steam_id = steamId, p_amount = body.Amount });
            p.Add("p_balance", dbType: DbType.Int32, direction: ParameterDirection.Output);
            await conn.ExecuteAsync("sp_spend_coins", p, commandType: CommandType.StoredProcedure);
            return TypedResults.Ok(new { balance = p.Get<int>("p_balance") });
        });
    }

    private static async Task<IResult> PlaceOrder(NewOrder body, MySqlDataSource db)
    {
        if (!IsSteamId(body.SteamId)) return Invalid("steamId", "SteamID64 tem 17 dígitos");
        if (body.Items is not { Count: > 0 }) return Invalid("items", "o carrinho está vazio");

        return await Guard(async () =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var p = new DynamicParameters(new
            {
                p_steam_id = body.SteamId,
                p_items = JsonSerializer.Serialize(body.Items.Select(i => new { sku = i.Sku, qty = i.Qty })),
                p_coupon = string.IsNullOrWhiteSpace(body.Coupon) ? null : body.Coupon.Trim().ToUpperInvariant(),
            });
            p.Add("p_order_id", dbType: DbType.Int64, direction: ParameterDirection.Output);
            await conn.ExecuteAsync("sp_place_order", p, commandType: CommandType.StoredProcedure);
            var id = p.Get<long>("p_order_id");
            return TypedResults.Created($"/api/orders/{id}", await LoadOrder(conn, id));
        });
    }

    private static async Task<IResult> GetOrder(long id, MySqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        var order = await LoadOrder(conn, id);
        return order is null ? TypedResults.NotFound() : TypedResults.Ok(order);
    }

    private static async Task<IResult> CancelOrder(long id, MySqlDataSource db) =>
        await Guard(async () =>
        {
            await using var conn = await db.OpenConnectionAsync();
            await conn.ExecuteAsync("sp_cancel_order", new { p_order_id = id }, commandType: CommandType.StoredProcedure);
            return TypedResults.NoContent();
        });

    private static async Task<IResult> PaymentWebhook(HttpRequest request, MySqlDataSource db, IOptions<PaymentOptions> options)
    {
        // a assinatura é sobre os bytes exatos do corpo, então lê o corpo cru antes de desserializar
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        var raw = buffer.ToArray();
        if (!WebhookSignature.IsValid(options.Value.WebhookSecret, raw, request.Headers["X-Signature"]))
            return TypedResults.Unauthorized();

        PaymentNotice? notice;
        try
        {
            notice = JsonSerializer.Deserialize<PaymentNotice>(raw, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return Invalid("body", "JSON inválido");
        }
        if (notice is null || notice.OrderId <= 0 || string.IsNullOrWhiteSpace(notice.Reference) || notice.AmountCents <= 0)
            return Invalid("body", "orderId, provider, reference e amountCents são obrigatórios");

        return await Guard(async () =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var p = new DynamicParameters(new
            {
                p_order_id = notice.OrderId,
                p_provider = notice.Provider,
                p_ref = notice.Reference,
                p_amount = notice.AmountCents,
            });
            p.Add("p_result", dbType: DbType.String, size: 16, direction: ParameterDirection.Output);
            await conn.ExecuteAsync("sp_confirm_payment", p, commandType: CommandType.StoredProcedure);
            return TypedResults.Ok(new { status = p.Get<string>("p_result") });
        });
    }

    private static async Task<IResult> SalesReport(MySqlDataSource db, DateOnly? from = null, DateOnly? to = null)
    {
        await using var conn = await db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<SalesDay>("""
            SELECT day, orders, CAST(revenue_cents AS SIGNED) AS RevenueCents, CAST(discount_cents AS SIGNED) AS DiscountCents
            FROM v_sales_daily
            WHERE (@from IS NULL OR day >= @from) AND (@to IS NULL OR day <= @to)
            ORDER BY day
            """, new { from = from?.ToDateTime(TimeOnly.MinValue), to = to?.ToDateTime(TimeOnly.MinValue) });
        return TypedResults.Ok(rows);
    }

    private static async Task<IResult> TopProducts(MySqlDataSource db, int limit = 5)
    {
        if (limit is < 1 or > 50) return Invalid("limit", "de 1 a 50");
        await using var conn = await db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<TopProduct>("""
            SELECT sku, name, CAST(units AS SIGNED) AS Units, CAST(gross_cents AS SIGNED) AS GrossCents
            FROM v_top_products ORDER BY gross_cents DESC LIMIT @limit
            """, new { limit });
        return TypedResults.Ok(rows);
    }

    private static async Task<OrderDto?> LoadOrder(MySqlConnection conn, long id)
    {
        var head = await conn.QuerySingleOrDefaultAsync<OrderDto>("""
            SELECT o.id, c.steam_id AS SteamId, o.status, o.subtotal_cents AS SubtotalCents,
                   o.discount_cents AS DiscountCents, o.total_cents AS TotalCents, o.coupon_code AS CouponCode,
                   o.created_at AS CreatedAt, o.paid_at AS PaidAt
            FROM orders o JOIN customers c ON c.id = o.customer_id
            WHERE o.id = @id
            """, new { id });
        if (head is null) return null;
        var items = await conn.QueryAsync<OrderItemDto>("""
            SELECT p.sku, p.name, i.quantity, i.unit_price_cents AS UnitPriceCents
            FROM order_items i JOIN products p ON p.id = i.product_id
            WHERE i.order_id = @id ORDER BY p.sku
            """, new { id });
        return head with { Items = items.AsList() };
    }

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (MySqlException e) when (StoreErrors.ToResult(e) is { } result)
        {
            return result;
        }
    }

    private static bool IsSteamId(string? s) => s is { Length: 17 } && s.All(char.IsAsciiDigit);

    private static IResult Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
