namespace Api.Common;

// 저장·반환하는 미디어 절대 URL 의 베이스. 리버스 프록시 뒤에서 ForwardedHeaders 미설정이면 req.Scheme/Host 가
// 내부(http://내부호스트) 값이라 깨진 URL 이 DB 에 영구 저장된다. Api:PublicBaseUrl 설정 시 그 값을, 아니면
// 요청 스킴/호스트를 사용 — 배포에서 둘 중 하나만 맞춰도 올바른 URL 이 나오게(NON-254).
public static class PublicUrl
{
    public static string Base(IConfiguration config, HttpRequest req)
    {
        var configured = config["Api:PublicBaseUrl"];
        return string.IsNullOrWhiteSpace(configured)
            ? $"{req.Scheme}://{req.Host}"
            : configured.TrimEnd('/');
    }
}
