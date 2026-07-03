using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Admin.Components;

/// 관리자 페이지/컴포넌트 공용 베이스 (NON-101). 위험 작업 confirm·중복클릭 방지·DB 예외 배너를 한곳에.
public abstract class AdminPageBase : ComponentBase
{
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    /// 조치 실행 중 여부 — 버튼 disabled 바인딩용(중복 클릭 방지).
    protected bool Busy { get; private set; }

    /// 마지막 작업/로드 실패 메시지(있으면 배너로 노출). 자연어만, 내부 예외는 숨김.
    protected string? Error { get; private set; }

    /// 위험/조치 실행: (confirm 있으면) 확인 → busy 잠금 → try/catch. 크래시·중복클릭·무피드백 방지.
    /// confirm 문자열이 있으면 브라우저 confirm 으로 한 번 더 묻는다(사용자 트리거라 JS 사용 가능).
    protected async Task RunAsync(Func<Task> action, string? confirm = null)
    {
        if (Busy) return;
        if (confirm is not null && !await JS.InvokeAsync<bool>("confirm", confirm)) return;

        Busy = true;
        Error = null;
        StateHasChanged(); // 즉시 disabled 반영
        try
        {
            await action();
        }
        catch (Exception)
        {
            Error = "작업 처리 중 오류가 발생했습니다. 잠시 후 다시 시도해주세요.";
        }
        finally
        {
            Busy = false;
        }
    }

    /// 데이터 로드 가드: 실패해도 페이지 크래시 대신 에러 배너만.
    protected async Task LoadAsync(Func<Task> load)
    {
        Error = null;
        try
        {
            await load();
        }
        catch (Exception)
        {
            Error = "데이터를 불러오지 못했습니다. 잠시 후 다시 시도해주세요.";
        }
    }
}
