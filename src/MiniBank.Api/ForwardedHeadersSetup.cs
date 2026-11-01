using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace MiniBank.Api;

internal static class ForwardedHeadersSetup
{
    /// <summary>
    /// <c>X-Forwarded-For</c> принимается только от прокси из сетей <c>ForwardedHeaders:TrustedNetworks</c> (CIDR).
    /// Без доверенных сетей заголовок игнорируется: иначе клиент подменой адреса обходил бы лимит запросов.
    /// Значение — массив (<c>:0</c>, <c>:1</c>…) либо одна строка со списком через запятую.
    /// </summary>
    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();

        foreach (var cidr in ReadTrustedNetworks(configuration))
            options.KnownNetworks.Add(ParseNetwork(cidr));

        // Пустые списки middleware трактует как «доверять всем», поэтому без сетей заголовки не обрабатываются вовсе.
        options.ForwardedHeaders = options.KnownNetworks.Count == 0
            ? ForwardedHeaders.None
            : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    }

    private static IEnumerable<string> ReadTrustedNetworks(IConfiguration configuration)
    {
        var section = configuration.GetSection("ForwardedHeaders:TrustedNetworks");
        var items = section.Value is { Length: > 0 } single
            ? single.Split(',')
            : section.Get<string[]>() ?? [];
        return items.Select(item => item.Trim()).Where(item => item.Length > 0);
    }

    private static Microsoft.AspNetCore.HttpOverrides.IPNetwork ParseNetwork(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var address) && int.TryParse(parts[1], out var prefix))
            return new Microsoft.AspNetCore.HttpOverrides.IPNetwork(address, prefix);
        throw new InvalidOperationException($"ForwardedHeaders:TrustedNetworks contains an invalid CIDR: '{cidr}'.");
    }
}
