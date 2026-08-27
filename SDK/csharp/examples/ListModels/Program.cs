// 连接 NanaLive，鉴权后打印模型目录。
//
// 运行：`dotnet run --project examples/ListModels`（需要 NanaLive 正在
// 运行；没有服务端时会报告连接错误）。

using Nanalive.Sdk;

var options = new ConnectOptions
{
    Identity = new Identity(
        "dev.example.nanalive-csharp-demo",
        "NanaLive C# Demo",
        "Example",
        "0.1.0",
        ["model.read"]),
    OnToken = token => Console.WriteLine($"首次签发的 token（请持久化，下次直接传入）: {token}"),
};

await using var connection = await NanaLiveConnection.ConnectAsync(options);
await connection.Client.AuthenticateAsync();
var models = await connection.Client.ListModelsAsync();
Console.WriteLine($"模型目录: {models}");
