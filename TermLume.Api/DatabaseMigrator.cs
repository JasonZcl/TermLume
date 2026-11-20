using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace TermLume.Api;

/// <summary>在事务和部署锁内应用嵌入 SQL；已应用文件的内容必须保持不变，包括注释。</summary>
public static class DatabaseMigrator
{
    public static async Task ApplyAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var initialize = new NpgsqlCommand("SELECT pg_advisory_xact_lock(732861041); CREATE TABLE IF NOT EXISTS public.termlume_migrations (version text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", connection, transaction))
            await initialize.ExecuteNonQueryAsync(cancellationToken);
        var assembly = typeof(DatabaseMigrator).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("TermLume.Api.Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = (await reader.ReadToEndAsync(cancellationToken)).Replace("\r\n", "\n");
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            await using var query = new NpgsqlCommand("SELECT checksum FROM public.termlume_migrations WHERE version=$1", connection, transaction);
            query.Parameters.AddWithValue(name);
            var saved = await query.ExecuteScalarAsync(cancellationToken) as string;
            if (saved is not null)
            {
                if (saved != checksum) throw new InvalidDataException("An applied database migration was modified.");
                continue;
            }
            await using (var apply = new NpgsqlCommand(sql, connection, transaction)) await apply.ExecuteNonQueryAsync(cancellationToken);
            await using var record = new NpgsqlCommand("INSERT INTO public.termlume_migrations(version, checksum) VALUES($1,$2)", connection, transaction);
            record.Parameters.AddWithValue(name); record.Parameters.AddWithValue(checksum);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
