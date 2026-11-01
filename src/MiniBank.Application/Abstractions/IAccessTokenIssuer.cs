using MiniBank.Application.Auth;
using MiniBank.Domain;

namespace MiniBank.Application.Abstractions;

public interface IAccessTokenIssuer
{
    AccessToken Issue(User user, DateTimeOffset now);
}
