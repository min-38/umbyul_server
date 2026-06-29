using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Api.Auth;

/// <summary>
/// Supabase는 표준 OIDC 디스커버리 문서를 제공하지 않고 JWKS 엔드포인트만 노출한다.
/// JWKS JSON을 받아 서명 키만 채운 OpenIdConnectConfiguration으로 변환해
/// JwtBearer의 ConfigurationManager(캐시 + 주기적 갱신 → 키 회전 대응)에 물린다.
/// </summary>
public sealed class JwksConfigurationRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel);
        var config = new OpenIdConnectConfiguration { JsonWebKeySet = new JsonWebKeySet(json) };
        foreach (var key in config.JsonWebKeySet.GetSigningKeys())
        {
            config.SigningKeys.Add(key);
        }
        return config;
    }
}
