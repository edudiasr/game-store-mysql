using System.Security.Cryptography;
using System.Text;

namespace GameStore.Api.Payments;

/// <summary>
/// O provedor de pagamento assina o corpo da notificação com HMAC-SHA256 usando um segredo
/// combinado. Sem conferir a assinatura, qualquer um podia mandar "pedido pago" pra API.
/// </summary>
public static class WebhookSignature
{
    public static string Sign(string secret, ReadOnlySpan<byte> body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    public static bool IsValid(string secret, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header)) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(secret, body));
        var received = Encoding.ASCII.GetBytes(header.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, received);
    }
}
