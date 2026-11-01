namespace MiniBank.Application.Auth;

public sealed class AuthOptions
{
    /// <summary>Адрес веб-клиента без завершающего «/»: на него ведёт ссылка из письма.</summary>
    public string WebBaseUrl { get; init; } = string.Empty;
}
