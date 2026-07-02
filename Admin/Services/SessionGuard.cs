namespace Admin.Services;

/// 진행 중인 관리자 작업 추적(NON-53). 세션 만료 시 작업이 끝날 때까지 로그아웃을 미루기 위함.
/// 스코프드 = Blazor 서킷(접속)당 1개. 액션 핸들러는 using (Guard.Work()) 로 감싼다.
public sealed class SessionGuard
{
    private int _busy;

    public bool Busy => Volatile.Read(ref _busy) > 0;

    public IDisposable Work()
    {
        Interlocked.Increment(ref _busy);
        return new Releaser(this);
    }

    private void Release() => Interlocked.Decrement(ref _busy);

    private sealed class Releaser(SessionGuard guard) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) guard.Release();
        }
    }
}
