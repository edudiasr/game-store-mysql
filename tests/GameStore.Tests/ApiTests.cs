using System.Net;
using System.Net.Http.Json;
using System.Text;
using GameStore.Api.Endpoints;
using GameStore.Api.Payments;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GameStore.Tests;

// Sobe a API inteira apontando pra um banco de teste e testa pelo HTTP.
public sealed class ApiTests : IAsyncLifetime
{
    private const string Secret = "segredo-de-teste";
    private TestDatabase _db = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Store", _db.ConnectionString);
            b.UseSetting("Payments:WebhookSecret", Secret);
        });
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    private Task<HttpResponseMessage> SendWebhook(object notice, string? secret = Secret)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(notice);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (secret is not null) request.Headers.Add("X-Signature", WebhookSignature.Sign(secret, Encoding.UTF8.GetBytes(body)));
        return _client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task FullPurchase_FromSignupToCoinsInTheWallet()
    {
        var signup = await _client.PostAsJsonAsync("/api/customers", new NewCustomer("76561190000000099", "Tatu", null), Ct);
        Assert.Equal(HttpStatusCode.Created, signup.StatusCode);

        var created = await _client.PostAsJsonAsync("/api/orders",
            new NewOrder("76561190000000099", [new CartItem("COINS_1200", 1)], "bemvindo10"), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = await created.Content.ReadFromJsonAsync<OrderDto>(Ct);
        Assert.Equal("pending", order!.Status);
        Assert.Equal(1791, order.TotalCents); // 19,90 - 10%

        var paid = await SendWebhook(new { orderId = order.Id, provider = "pix", reference = "e2e-1", amountCents = order.TotalCents });
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        var wallet = await _client.GetFromJsonAsync<WalletDto>("/api/customers/76561190000000099/wallet", Ct);
        Assert.Equal(1200, wallet!.Balance);
        Assert.Equal("purchase", Assert.Single(wallet.Recent).Reason);

        var spend = await _client.PostAsJsonAsync("/api/customers/76561190000000099/wallet/spend", new SpendRequest(200), Ct);
        Assert.Equal(HttpStatusCode.OK, spend.StatusCode);
    }

    [Fact]
    public async Task Webhook_WithoutAValidSignature_IsRejected()
    {
        var notice = new { orderId = 1, provider = "pix", reference = "x", amountCents = 990 };
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendWebhook(notice, secret: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendWebhook(notice, secret: "outro-segredo")).StatusCode);
    }

    [Fact]
    public async Task RuleErrors_BecomeProblemDetails_WithTheRightStatus()
    {
        var noStock = await _client.PostAsJsonAsync("/api/orders",
            new NewOrder("76561190000000001", [new CartItem("KIT_LENDA", 50)], null), Ct);
        Assert.Equal(HttpStatusCode.Conflict, noStock.StatusCode);
        Assert.Contains("estoque insuficiente", await noStock.Content.ReadAsStringAsync(Ct));

        var unknown = await _client.PostAsJsonAsync("/api/orders",
            new NewOrder("76561199999999999", [new CartItem("COINS_500", 1)], null), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var badSteam = await _client.PostAsJsonAsync("/api/orders", new NewOrder("123", [new CartItem("COINS_500", 1)], null), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badSteam.StatusCode);

        var duplicate = await _client.PostAsJsonAsync("/api/customers", new NewCustomer("76561190000000001", "Outro", null), Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Reports_ReflectPaidOrders()
    {
        var created = await _client.PostAsJsonAsync("/api/orders",
            new NewOrder("76561190000000002", [new CartItem("VIP_30D", 2)], null), Ct);
        var order = await created.Content.ReadFromJsonAsync<OrderDto>(Ct);
        await SendWebhook(new { orderId = order!.Id, provider = "card", reference = "c-1", amountCents = order.TotalCents });

        var top = await _client.GetFromJsonAsync<List<TopProduct>>("/api/reports/top-products?limit=3", Ct);
        Assert.Equal("VIP_30D", top![0].Sku);
        Assert.Equal(2, top[0].Units);

        var sales = await _client.GetFromJsonAsync<List<SalesDay>>("/api/reports/sales", Ct);
        Assert.Equal(4980, Assert.Single(sales!).RevenueCents);
    }

    [Fact]
    public async Task Products_ListsTheCatalog()
    {
        var products = await _client.GetFromJsonAsync<List<ProductDto>>("/api/products", Ct);
        Assert.Equal(6, products!.Count);
        Assert.Contains(products, p => p.Sku == "KIT_LENDA" && p.Stock == 5);
    }
}
