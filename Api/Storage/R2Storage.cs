using Amazon.S3;
using Amazon.S3.Model;

namespace Api.Storage;

/// Cloudflare R2 (S3 호환) 스토리지. STORAGE 설정으로 구성. 싱글톤.
/// 공개 URL이 설정에 없어 서빙은 API 프록시(GetAsync). 시크릿은 user-secrets/env.
public sealed class R2Storage
{
    private readonly IAmazonS3? _s3;
    private readonly string? _bucket;

    public R2Storage(IConfiguration config)
    {
        var s = config.GetSection("STORAGE");
        var url = s["URL"];          // S3 엔드포인트 (https://<account>.r2.cloudflarestorage.com)
        var key = s["PUBLIC_KEY"];   // Access Key ID
        var secret = s["SECRET_KEY"];
        _bucket = s["BUCKET_NAME"];

        if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(key)
            && !string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(_bucket))
        {
            _s3 = new AmazonS3Client(key, secret, new AmazonS3Config
            {
                ServiceURL = url,
                ForcePathStyle = true,
                // R2는 AWS SDK v4 기본 무결성 체크섬을 일부 거부 → 필요 시에만 계산.
                RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
            });
        }
    }

    public bool Configured => _s3 is not null;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        await _s3!.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            // R2는 STREAMING-AWS4-HMAC-SHA256-PAYLOAD 미구현 → UNSIGNED-PAYLOAD 로 전송.
            DisablePayloadSigning = true,
        }, ct);
    }

    /// 단일 객체 삭제(없으면 무시). 아바타 교체 시 이전 파일 정리(LEG-3).
    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        if (_s3 is null) return;
        try { await _s3.DeleteObjectAsync(_bucket, key, ct); }
        catch (AmazonS3Exception) { /* 이미 없음 등 무시 */ }
    }

    /// 프리픽스 아래 전체 삭제(계정 탈퇴 시 avatars/{uid}/ 정리 — LEG-3). 페이지네이션 처리.
    public async Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        if (_s3 is null) return;
        try
        {
            string? token = null;
            do
            {
                var list = await _s3.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    Prefix = prefix,
                    ContinuationToken = token,
                }, ct);
                if (list.S3Objects is { Count: > 0 } objs)
                    await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
                    {
                        BucketName = _bucket,
                        Objects = objs.Select(o => new KeyVersion { Key = o.Key }).ToList(),
                    }, ct);
                token = list.IsTruncated == true ? list.NextContinuationToken : null;
            } while (token is not null);
        }
        catch (AmazonS3Exception) { /* 스토리지 오류는 탈퇴를 막지 않음 */ }
    }

    /// 객체 스트림 + content-type. 없으면 null.
    public async Task<(Stream Content, string ContentType)?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            var res = await _s3!.GetObjectAsync(_bucket, key, ct);
            return (res.ResponseStream, res.Headers.ContentType ?? "application/octet-stream");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
