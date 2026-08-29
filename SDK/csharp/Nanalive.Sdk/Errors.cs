namespace Nanalive.Sdk;

/// <summary>服务端返回 <c>messageType == "APIError"</c> 时抛出。</summary>
public sealed class NanaLiveApiException : Exception
{
    /// <summary>服务端 <c>data.errorCode</c>。</summary>
    public object? Code { get; }

    public NanaLiveApiException(string message, object? code = null)
        : base(message)
    {
        Code = code;
    }
}

/// <summary>鉴权时服务端没有签发 token。</summary>
public sealed class AuthenticationTokenMissingException : Exception
{
    public AuthenticationTokenMissingException()
        : base("authentication_token_missing")
    {
    }
}

/// <summary>连接层错误：未连接发起请求、断线后挂起请求失败等。</summary>
public class NanaLiveConnectionException : Exception
{
    public NanaLiveConnectionException(string message)
        : base(message)
    {
    }
}

/// <summary>请求在超时时间内没有等到响应。</summary>
public sealed class NanaLiveRequestTimeoutException : NanaLiveConnectionException
{
    public NanaLiveRequestTimeoutException()
        : base("request_timeout")
    {
    }
}
