using Npgsql;

namespace TermLume.Api;

/// <summary>启动时及每小时清理过期业务日志，不依赖账号再次登录或上传。</summary>
public sealed class ServerLogRetentionService(NpgsqlDataSource database, ILogger<ServerLogRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                // 固定策略在数据库中执行；运行角色仍无审计表的 UPDATE / DELETE 权限。
                await using var command = database.CreateCommand("SELECT termlume.prune_expired_logs()");
                command.CommandTimeout = 30;
                await command.ExecuteNonQueryAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is NpgsqlException or TimeoutException)
            {
                // 不记录异常正文，避免连接串或 SQL 参数进入运行日志；下一周期重试。
                logger.LogWarning("Server log retention failed: {ErrorType}", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
