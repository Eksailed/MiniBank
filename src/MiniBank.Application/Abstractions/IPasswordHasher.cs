namespace MiniBank.Application.Abstractions;

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);

    /// <summary>
    /// Корректный хэш неизвестного пароля: вход с несуществующим email проверяется по нему,
    /// чтобы время ответа не выдавало, есть ли такой пользователь.
    /// </summary>
    string DummyHash { get; }
}
