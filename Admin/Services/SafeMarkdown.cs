using Markdig;

namespace Admin.Services;

/// 관리자 미리보기용 마크다운 렌더러. raw HTML 을 비활성화해 DB 에 저장된 &lt;script&gt; 등이
/// (MarkupString) 로 관리자 오리진에서 실행되는 저장형 XSS 를 차단한다(ADM-5).
/// 공개 웹은 react-markdown 이 raw HTML 을 이스케이프하므로 미리보기 동작도 이와 일치.
public static class SafeMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public static string ToHtml(string? markdown) => Markdown.ToHtml(markdown ?? "", Pipeline);
}
