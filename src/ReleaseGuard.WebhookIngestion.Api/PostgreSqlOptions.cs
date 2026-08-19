using Npgsql;

namespace ReleaseGuard.WebhookIngestion.Api;

public sealed class PostgreSqlOptions
{
    public const string SectionName = "PostgreSql";

    public string ConnectionString { get; init; } = string.Empty;

    public bool ApplyMigrationsOnStartup { get; init; }

    public static bool HasValidConnectionString(PostgreSqlOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return false;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
            return !string.IsNullOrWhiteSpace(builder.Host) &&
                   !string.IsNullOrWhiteSpace(builder.Database);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
