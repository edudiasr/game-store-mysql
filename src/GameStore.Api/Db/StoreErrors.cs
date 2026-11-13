using MySqlConnector;

namespace GameStore.Api.Db;

/// <summary>
/// Traduz erro do banco pra resposta HTTP. As procedures avisam regra quebrada com
/// SIGNAL SQLSTATE '45000' e uma mensagem; o resto é erro de verdade e vira 500.
/// </summary>
public static class StoreErrors
{
    private const int DuplicateKey = 1062;

    public static IResult? ToResult(MySqlException e)
    {
        if (e.SqlState == "45000")
        {
            var status = e.Message switch
            {
                var m when m.Contains("não encontrado") => StatusCodes.Status404NotFound,
                var m when m.Contains("estoque") || m.Contains("esgotado") || m.Contains("saldo")
                    || m.Contains("não está") || m.Contains("já foi usado") || m.Contains("só dá pra") => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status422UnprocessableEntity,
            };
            return TypedResults.Problem(e.Message, statusCode: status);
        }
        if (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry || e.Number == DuplicateKey)
            return TypedResults.Problem("já existe um registro com esses dados", statusCode: StatusCodes.Status409Conflict);
        // 3140/3141: JSON inválido; 3665/3666: campo obrigatório faltando no JSON_TABLE
        if (e.Number is 3140 or 3141 or 3665 or 3666)
            return TypedResults.Problem("carrinho em formato inválido", statusCode: StatusCodes.Status422UnprocessableEntity);
        return null;
    }
}
