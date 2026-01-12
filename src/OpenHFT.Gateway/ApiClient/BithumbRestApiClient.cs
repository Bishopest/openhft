using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Gateway.ApiClient;

public class BithumbRestApiClient : BaseRestApiClient
{
    public override ExchangeEnum SourceExchange => ExchangeEnum.BITHUMB;

    public BithumbRestApiClient(
        ILogger<BithumbRestApiClient> logger,
        IInstrumentRepository instrumentRepository,
        HttpClient httpClient,
        ProductType productType,
        ExecutionMode mode,
        string? apiSecret = null,
        string? apiKey = null)
        : base(logger, instrumentRepository, httpClient, productType, mode, apiSecret, apiKey)
    {
    }

    protected override string GetBaseUrl(ExecutionMode mode)
    {
        // 빗썸은 테스트넷과 실서버 URL이 동일하거나 별도 공지되므로 기본 주소 사용
        return "https://api.bithumb.com";
    }

    public override async Task<long> GetServerTimeAsync(CancellationToken cancellationToken = default)
    {
        // 빗썸은 별도의 /time 엔드포인트가 없으므로 공용 시세 API의 timestamp를 활용합니다.
        var result = await SendRequestAsync<BithumbTickerResponse>(HttpMethod.Get, "/v1/ticker?markets=KRW-BTC", cancellationToken: cancellationToken);
        if (!result.IsSuccess || result.Data == null || result.Data.Count == 0)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        return result.Data[0].Timestamp;
    }

    protected override void AddSignatureToRequest(
        HttpRequestMessage request,
        string fullPath,
        string? queryString,
        Dictionary<string, object>? bodyParams)
    {
        // 1. 빗썸 전용 쿼리 스트링 빌더 (배열일 경우 key[]=value 형태 준수)
        string combinedParams = "";
        if (bodyParams != null && bodyParams.Count > 0)
        {
            combinedParams = BuildBithumbQueryString(bodyParams);
        }
        else if (!string.IsNullOrEmpty(queryString))
        {
            combinedParams = queryString;
        }

        // 2. Query Hash 생성 (SHA512 -> Hex String)
        string queryHash = "";
        if (!string.IsNullOrEmpty(combinedParams))
        {
            using var sha512 = SHA512.Create();
            byte[] hashBytes = sha512.ComputeHash(Encoding.UTF8.GetBytes(combinedParams));
            queryHash = Convert.ToHexString(hashBytes).ToLower(); // Hex 인코딩
        }

        // 3. JWT 페이로드 (query_hash가 있을 때만 포함)
        var payload = new Dictionary<string, object>
        {
            { "access_key", ApiKey! },
            { "nonce", Guid.NewGuid().ToString() },
            { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        };

        if (!string.IsNullOrEmpty(queryHash))
        {
            payload.Add("query_hash", queryHash);
            payload.Add("query_hash_alg", "SHA512");
        }

        string jwtToken = CreateJwtToken(payload); // 표준 JWT 생성 로직

        request.Headers.Add("Authorization", $"Bearer {jwtToken}");

        if (request.Method == HttpMethod.Post && bodyParams != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(bodyParams), Encoding.UTF8, "application/json");
        }
    }

    // 빗썸 특유의 key[]=value 포맷 지원 로직
    private string BuildBithumbQueryString(Dictionary<string, object> parameters)
    {
        var segments = new List<string>();
        foreach (var kvp in parameters)
        {
            if (kvp.Value is IEnumerable<string> list)
            {
                foreach (var val in list)
                    segments.Add($"{kvp.Key}[]={Uri.EscapeDataString(val)}");
            }
            else
            {
                segments.Add($"{kvp.Key}={Uri.EscapeDataString(kvp.Value.ToString()!)}");
            }
        }
        return string.Join("&", segments);
    }

    public string CreateJwtToken(object payload)
    {
        // Header: {"alg":"HS256","typ":"JWT"}
        string header = Base64UrlEncode("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
        string payloadJson = Base64UrlEncode(JsonSerializer.Serialize(payload));

        // Signature: HMACSHA256(header.payload, secret)
        string signatureSource = $"{header}.{payloadJson}";
        string signature = Base64UrlEncodeFromBytes(ComputeHmacSha256(signatureSource));

        return $"{header}.{payloadJson}.{signature}";
    }

    private byte[] ComputeHmacSha256(string message)
    {
        var keyBytes = Encoding.UTF8.GetBytes(ApiSecret!);
        using var hmac = new HMACSHA256(keyBytes);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
    }

    private string Base64UrlEncode(string input) => Base64UrlEncodeFromBytes(Encoding.UTF8.GetBytes(input));

    private string Base64UrlEncodeFromBytes(byte[] input)
    {
        return Convert.ToBase64String(input)
            .Replace("=", "")
            .Replace('+', '-')
            .Replace('/', '_');
    }

    // 빗썸 응답 모델 (ServerTime용)
    private class BithumbTickerResponse : List<BithumbTickerData> { }

    private class BithumbTickerData
    {
        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }
}
