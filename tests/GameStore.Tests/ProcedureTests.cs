using MySqlConnector;

namespace GameStore.Tests;

// Testa as regras que moram no banco (procedures, constraints e views) direto no MySQL.
public sealed class ProcedureTests : IAsyncLifetime
{
    private const string Lobo = "76561190000000001";
    private const string Corvo = "76561190000000002";
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await TestDatabase.CreateAsync();
    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private static async Task<string> RuleError(Func<Task> action)
    {
        var e = await Assert.ThrowsAsync<MySqlException>(action);
        Assert.Equal("45000", e.SqlState);
        return e.Message;
    }

    [Fact]
    public async Task PlaceOrder_ComputesTotals_AppliesCoupon_AndReservesStock()
    {
        var id = await _db.PlaceOrderAsync(Lobo, """[{"sku":"KIT_INICIAL","qty":2},{"sku":"COINS_500","qty":1}]""", "BEMVINDO10");

        // 2 x 7,90 + 9,90 = 25,70; 10% = 2,57 (arredonda pra baixo)
        Assert.Equal(2570, await _db.ScalarAsync<int>($"SELECT subtotal_cents FROM orders WHERE id = {id}"));
        Assert.Equal(257, await _db.ScalarAsync<int>($"SELECT discount_cents FROM orders WHERE id = {id}"));
        Assert.Equal(2313, await _db.ScalarAsync<int>($"SELECT total_cents FROM orders WHERE id = {id}"));
        Assert.Equal(48, await _db.ScalarAsync<int>("SELECT stock FROM products WHERE sku = 'KIT_INICIAL'"));
        Assert.Equal(1, await _db.ScalarAsync<int>("SELECT used FROM coupons WHERE code = 'BEMVINDO10'"));
    }

    [Fact]
    public async Task SameSkuTwiceInTheCart_BecomesOneLine()
    {
        var id = await _db.PlaceOrderAsync(Lobo, """[{"sku":"KIT_INICIAL","qty":1},{"sku":"KIT_INICIAL","qty":2}]""");
        Assert.Equal(3, await _db.ScalarAsync<int>($"SELECT quantity FROM order_items WHERE order_id = {id}"));
    }

    [Fact]
    public async Task NotEnoughStock_RollsBackEverything()
    {
        var message = await RuleError(() => _db.PlaceOrderAsync(Lobo, """[{"sku":"KIT_LENDA","qty":6}]""", "BEMVINDO10"));

        Assert.Equal("estoque insuficiente", message);
        Assert.Equal(5, await _db.ScalarAsync<int>("SELECT stock FROM products WHERE sku = 'KIT_LENDA'"));
        Assert.Equal(0, await _db.ScalarAsync<int>("SELECT COUNT(*) FROM orders"));
        Assert.Equal(0, await _db.ScalarAsync<int>("SELECT used FROM coupons WHERE code = 'BEMVINDO10'"));
    }

    [Theory]
    [InlineData("""[{"sku":"NAO_EXISTE","qty":1}]""", null, "produto inexistente ou fora de venda")]
    [InlineData("""[{"sku":"COINS_500","qty":0}]""", null, "quantidade inválida (1 a 100)")]
    [InlineData("[]", null, "carrinho vazio")]
    [InlineData("""[{"sku":"COINS_500","qty":1}]""", "CUPOM_FALSO", "cupom inválido, vencido ou esgotado")]
    public async Task InvalidOrders_AreRejectedWithAClearMessage(string items, string? coupon, string expected)
    {
        Assert.Equal(expected, await RuleError(() => _db.PlaceOrderAsync(Lobo, items, coupon)));
    }

    [Fact]
    public async Task CouponWithUsageLimit_StopsAtTheLimit()
    {
        await _db.ExecAsync("UPDATE coupons SET max_uses = 1 WHERE code = 'LANCAMENTO'");
        await _db.PlaceOrderAsync(Lobo, """[{"sku":"COINS_500","qty":1}]""", "LANCAMENTO");
        Assert.Equal("cupom inválido, vencido ou esgotado",
            await RuleError(() => _db.PlaceOrderAsync(Corvo, """[{"sku":"COINS_500","qty":1}]""", "LANCAMENTO")));
    }

    [Fact]
    public async Task TwoBuyersForTheLastItem_OnlyOneGetsIt()
    {
        await _db.ExecAsync("UPDATE products SET stock = 1 WHERE sku = 'KIT_LENDA'");

        // as duas compras saem ao mesmo tempo, cada uma na sua conexão
        var attempts = await Task.WhenAll(
            Try(() => _db.PlaceOrderAsync(Lobo, """[{"sku":"KIT_LENDA","qty":1}]""")),
            Try(() => _db.PlaceOrderAsync(Corvo, """[{"sku":"KIT_LENDA","qty":1}]""")));

        Assert.Equal(1, attempts.Count(ok => ok));
        Assert.Equal(0, await _db.ScalarAsync<int>("SELECT stock FROM products WHERE sku = 'KIT_LENDA'"));
        Assert.Equal(1, await _db.ScalarAsync<int>("SELECT COUNT(*) FROM orders"));

        static async Task<bool> Try(Func<Task> f)
        {
            try { await f(); return true; }
            catch (MySqlException e) when (e.SqlState == "45000") { return false; }
        }
    }

    [Fact]
    public async Task Payment_CreditsCoinsOnce_EvenIfTheNotificationArrivesTwice()
    {
        var id = await _db.PlaceOrderAsync(Lobo, """[{"sku":"COINS_1200","qty":2},{"sku":"VIP_30D","qty":1}]""");
        var total = await _db.ScalarAsync<int>($"SELECT total_cents FROM orders WHERE id = {id}");

        Assert.Equal("paid", await _db.ConfirmPaymentAsync(id, "pix-123", total));
        Assert.Equal("duplicate", await _db.ConfirmPaymentAsync(id, "pix-123", total));

        Assert.Equal(1, await _db.ScalarAsync<int>("SELECT COUNT(*) FROM payments"));
        Assert.Equal(2400, await _db.ScalarAsync<int>($"SELECT balance FROM v_wallet_balance WHERE steam_id = '{Lobo}'"));
        Assert.Equal("paid", await _db.ScalarAsync<string>($"SELECT status FROM orders WHERE id = {id}"));
    }

    [Fact]
    public async Task Payment_WithTheWrongAmount_IsRejected_AndOrderStaysPending()
    {
        var id = await _db.PlaceOrderAsync(Lobo, """[{"sku":"COINS_500","qty":1}]""");
        Assert.Equal("valor pago diferente do total do pedido", await RuleError(() => _db.ConfirmPaymentAsync(id, "pix-1", 1)));
        Assert.Equal("pending", await _db.ScalarAsync<string>($"SELECT status FROM orders WHERE id = {id}"));
    }

    [Fact]
    public async Task SamePaymentReference_CannotPayAnotherOrder()
    {
        var a = await _db.PlaceOrderAsync(Lobo, """[{"sku":"COINS_500","qty":1}]""");
        var b = await _db.PlaceOrderAsync(Corvo, """[{"sku":"COINS_500","qty":1}]""");
        await _db.ConfirmPaymentAsync(a, "pix-9", 990);
        Assert.Equal("esse pagamento já foi usado em outro pedido", await RuleError(() => _db.ConfirmPaymentAsync(b, "pix-9", 990)));
    }

    [Fact]
    public async Task Cancel_ReturnsStockAndCouponUse()
    {
        var id = await _db.PlaceOrderAsync(Lobo, """[{"sku":"KIT_LENDA","qty":2}]""", "LANCAMENTO");
        await _db.ExecAsync($"CALL sp_cancel_order({id})");

        Assert.Equal(5, await _db.ScalarAsync<int>("SELECT stock FROM products WHERE sku = 'KIT_LENDA'"));
        Assert.Equal(0, await _db.ScalarAsync<int>("SELECT used FROM coupons WHERE code = 'LANCAMENTO'"));
        Assert.Equal("cancelled", await _db.ScalarAsync<string>($"SELECT status FROM orders WHERE id = {id}"));
    }

    [Fact]
    public async Task SpendingCoins_InParallel_NeverGoesBelowZero()
    {
        var id = await _db.PlaceOrderAsync(Lobo, """[{"sku":"COINS_500","qty":1}]""");
        await _db.ConfirmPaymentAsync(id, "pix-1", 990);

        // saldo 500, dois gastos de 400 ao mesmo tempo: só um pode passar
        var results = await Task.WhenAll(TrySpend(), TrySpend());

        Assert.Equal(1, results.Count(ok => ok));
        Assert.Equal(100, await _db.ScalarAsync<int>($"SELECT balance FROM v_wallet_balance WHERE steam_id = '{Lobo}'"));

        async Task<bool> TrySpend()
        {
            try { await _db.SpendAsync(Lobo, 400); return true; }
            catch (MySqlException e) when (e.Message == "saldo insuficiente") { return false; }
        }
    }

    [Fact]
    public async Task Reports_CountOnlyPaidOrders()
    {
        var paid = await _db.PlaceOrderAsync(Lobo, """[{"sku":"COINS_3000","qty":1}]""");
        await _db.PlaceOrderAsync(Corvo, """[{"sku":"COINS_3000","qty":5}]"""); // fica pendente
        await _db.ConfirmPaymentAsync(paid, "pix-1", 4490);

        Assert.Equal(1, await _db.ScalarAsync<int>("SELECT SUM(orders) FROM v_sales_daily"));
        Assert.Equal(4490, await _db.ScalarAsync<int>("SELECT SUM(revenue_cents) FROM v_sales_daily"));
        Assert.Equal(1, await _db.ScalarAsync<int>("SELECT units FROM v_top_products WHERE sku = 'COINS_3000'"));
    }

    [Fact]
    public async Task Constraints_ProtectTheData_EvenWithoutTheProcedures()
    {
        // SteamID com letra, cupom de 95% e pedido "pago" sem data de pagamento são barrados pelo próprio banco
        await Assert.ThrowsAsync<MySqlException>(() => _db.ExecAsync("INSERT INTO customers (steam_id, nickname) VALUES ('7656119ABC0000001', 'x')"));
        await Assert.ThrowsAsync<MySqlException>(() => _db.ExecAsync("INSERT INTO coupons (code, percent_off) VALUES ('X', 95)"));
        var id = await _db.PlaceOrderAsync(Lobo, """[{"sku":"COINS_500","qty":1}]""");
        await Assert.ThrowsAsync<MySqlException>(() => _db.ExecAsync($"UPDATE orders SET status = 'paid' WHERE id = {id}"));
    }
}
